using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Commands;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region Types imbriqués

    private sealed class ThrottledTokenSink : IDisposable
    {
        private const int FrameMs = 32;

        private readonly Action<string> _onFlush;
        private readonly StringBuilder  _buf  = new();
        private readonly object         _gate = new();
        private readonly Timer          _timer;
        private bool _stopped;

        public ThrottledTokenSink(Action<string> onFlush)
        {
            _onFlush = onFlush;
            _timer   = new Timer(_ => Flush(), null, FrameMs, FrameMs);
        }

        public void Append(string token)
        {
            lock (_gate) { _buf.Append(token); }
        }

        private void Flush()
        {
            string chunk;
            lock (_gate)
            {
                if (_buf.Length == 0) return;
                chunk = _buf.ToString();
                _buf.Clear();
            }
            _onFlush(chunk);
        }

        // Drops any buffered-but-not-yet-flushed tokens without flushing them. Called on a stream
        // reset between agent iterations: without it, tokens still sitting in _buf when the bubble
        // is removed get flushed on the next timer tick and resurrect a stale bubble, producing a
        // duplicated response (the previous iteration's text prepended to the next).
        public void Reset()
        {
            lock (_gate) { _buf.Clear(); }
        }

        // Stops the timer and flushes remaining tokens. Call before RunOnVMContextAsync.
        public void Stop()
        {
            lock (_gate)
            {
                if (_stopped) return;
                _stopped = true;
            }
            _timer.Dispose();
            Flush();
        }

        public void Dispose() => Stop();
    }

    #endregion
}
