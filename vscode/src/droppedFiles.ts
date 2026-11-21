// What the chat reads of a file dropped from the system's file manager: one value for the webview, which never reads a
// larger file into the page, and for the extension, which says it is not attached.

/** The largest dropped file named `name` that is read, in bytes: the documents' own limit (20 MB; 64 MB for a Windows
 *  event log, whose default size is 21 MB, and an MSBuild binary log — `DocumentText.MaxBytesOf` in the Core). */
export function droppedFileLimit(name: string): number {
  // A video: the Core's VideoFrames.MaxBytes.
  if (/\.(mp4|mov|m4v|webm|mkv|avi|wmv)$/i.test(name)) {
    return 100 * 1024 * 1024;
  }
  return (/\.(evtx|binlog)$/i.test(name) ? 64 : 20) * 1024 * 1024;
}
