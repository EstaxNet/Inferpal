using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using System.Text;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A crash report reads first as what crashed (pari 149): the exception, what the program said, the faulting thread's stack,
/// the rest counted; recognised by its name and shape, so a log that quotes a crash stays a log.
/// </summary>
public class CrashReportTextTests
{
    [Fact]
    public void AWindowsErrorReport_FromThisMachine_ReadsItsSignature_AndSaysItHoldsNoStack()
    {
        // A real Report.wer (Explorer, a shell extension's access violation), identifiers trimmed, UTF-16 as Windows writes it.
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "crash", "Report.wer");
        var text = CrashReportText.Summarize("Report.wer", File.ReadAllText(path, Encoding.Unicode), 8000)!;
        Assert.StartsWith("Windows Error Report Report.wer: APPCRASH", text);
        Assert.Contains("of Explorer.EXE", text);
        Assert.Contains(", fatal.", text);
        Assert.Contains("c0000005 (access violation)", text);               // the code named, the signature's names as written
        Assert.Contains(@"Faulting module: C:\Users\dev\AppData\Local\Programs\Beyond Compare 5\BCShellEx64.dll", text);
        Assert.Contains("holds no stack", text);
    }

    private const string Ips = """
        {"app_name":"Shop","timestamp":"2026-11-20 10:31:02.00 +0100","app_version":"2.3","bug_type":"309","os_version":"macOS 15.1 (24B83)","name":"Shop"}
        {
          "procName" : "Shop", "captureTime" : "2026-11-20 10:31:01.8823 +0100",
          "bundleInfo" : { "CFBundleIdentifier" : "com.example.shop" },
          "exception" : { "type" : "EXC_CRASH", "signal" : "SIGABRT", "codes" : "0x0000000000000000, 0x0000000000000000" },
          "termination" : { "namespace" : "SIGNAL", "indicator" : "Abort trap: 6", "reasons" : [] },
          "asi" : { "libswiftCore.dylib" : ["Shop/Cart.swift:42: Fatal error: Index out of range"] },
          "faultingThread" : 0,
          "threads" : [
            { "triggered" : true, "queue" : "com.apple.main-thread", "frames" : [
              { "imageOffset" : 4321, "symbol" : "__pthread_kill", "symbolLocation" : 8, "imageIndex" : 1 },
              { "imageOffset" : 9876, "symbol" : "Cart.total()", "symbolLocation" : 120, "imageIndex" : 0, "sourceFile" : "Cart.swift", "sourceLine" : 42 } ] },
            { "frames" : [] }, { "frames" : [] } ],
          "usedImages" : [ { "name" : "Shop" }, { "name" : "libsystem_kernel.dylib" } ]
        }
        """;

    [Fact]
    public void AnAppleIps_ReadsTheExceptionWhatTheRuntimeSaid_AndTheCrashedThread()
    {
        var text = CrashReportText.Summarize("Shop-2026-11-20-103102.ips", Ips, 8000)!;
        Assert.StartsWith("Crash report Shop-2026-11-20-103102.ips: Shop 2.3 (com.example.shop), macOS 15.1 (24B83)", text);
        Assert.Contains("Exception: EXC_CRASH · SIGABRT", text);
        Assert.Contains("Termination: Abort trap: 6", text);
        Assert.Contains("Application said (libswiftCore.dylib): Shop/Cart.swift:42: Fatal error: Index out of range", text);
        Assert.Contains("Thread 0 crashed (com.apple.main-thread):", text);
        Assert.Contains("Cart.total() + 120 (Cart.swift:42)", text);
        Assert.Contains("2 other threads, 2 images loaded", text);
    }

    private const string Crash = """
        Process:               Shop [4242]
        Identifier:            com.example.shop
        Version:               2.3 (230)
        OS Version:            macOS 13.6 (22G120)

        Exception Type:        EXC_BAD_ACCESS (SIGSEGV)
        Exception Codes:       KERN_INVALID_ADDRESS at 0x0000000000000010
        Termination Reason:    Namespace SIGNAL, Code 11 Segmentation fault: 11

        Thread 0:: Dispatch queue: com.apple.main-thread
        0   libsystem_kernel.dylib        0x00007ff8 mach_msg2_trap + 10

        Thread 1 Crashed:
        0   Shop                          0x0000000104a1 Order.save() + 64 (Order.swift:88)
        1   Shop                          0x0000000104b2 closure #1 in Checkout.run() + 12

        Thread 1 crashed with X86 Thread State (64-bit):
          rax: 0x0000000000000000

        Binary Images:
               0x104000000 - Shop
               0x7ff800000 - libsystem_kernel.dylib
        """;

    [Fact]
    public void AnAppleTextCrash_ReadsTheCrashedThreadsBlock()
    {
        var text = CrashReportText.Summarize("Shop.crash", Crash, 8000)!;
        Assert.Contains("Exception Type:        EXC_BAD_ACCESS (SIGSEGV)", text);
        Assert.Contains("Thread 1 Crashed:", text);
        Assert.Contains("Order.save() + 64 (Order.swift:88)", text);
        Assert.DoesNotContain("mach_msg2_trap", text);                     // another thread: counted, not shown
        Assert.Contains("1 other threads, 2 binary images", text);
    }

    private const string Tombstone = """
        *** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***
        Build fingerprint: 'google/sdk_gphone64_x86_64/emu64x:14/UE1A/1234:userdebug/dev-keys'
        ABI: 'x86_64'
        Timestamp: 2026-11-20 10:31:02.123456789+0100
        pid: 4242, tid: 4242, name: example.shop  >>> com.example.shop <<<
        signal 6 (SIGABRT), code -1 (SI_QUEUE), fault addr --------
        Abort message: 'JNI DETECTED ERROR IN APPLICATION: use of deleted local reference'
            rax 0000000000000000  rbx 0000000000001092

        backtrace:
              #00 pc 000000000005b6c8  /apex/com.android.runtime/lib64/bionic/libc.so (abort+168)
              #01 pc 00000000000a1f20  /data/app/com.example.shop/lib/x86_64/libshop.so (Cart::total()+64)

        --- --- --- --- --- --- --- --- --- --- --- --- --- --- --- ---
        pid: 4242, tid: 4250, name: RenderThread  >>> com.example.shop <<<
        """;

    [Fact]
    public void AnAndroidTombstone_ReadsTheSignalTheAbortMessage_AndTheBacktrace()
    {
        var text = CrashReportText.Summarize("tombstone_03", Tombstone, 8000)!;
        Assert.Contains("signal 6 (SIGABRT)", text);
        Assert.Contains("Abort message: 'JNI DETECTED ERROR", text);
        Assert.Contains("#01 pc 00000000000a1f20  /data/app/com.example.shop/lib/x86_64/libshop.so (Cart::total()+64)", text);
        Assert.DoesNotContain("rbx", text);
        Assert.Contains("1 other threads", text);
    }

    private const string Anr = """
        ----- pid 4242 at 2026-11-20 10:31:02.123 -----
        Cmd line: com.example.shop

        "main" prio=5 tid=1 Blocked
          | group="main" sCount=1 ucsCount=0 flags=1 obj=0x72a0 self=0x7b
          at com.example.shop.Cart.total(Cart.java:42)
          - waiting to lock <0x0a1b2c3d> (a java.lang.Object) held by thread 14
          at com.example.shop.CartActivity.onResume(CartActivity.java:20)

        "Sync" prio=5 tid=14 Sleeping
          at java.lang.Thread.sleep(Native method)
          at com.example.shop.Sync.run(Sync.java:77)
          - locked <0x0a1b2c3d> (a java.lang.Object)

        "Signal Catcher" daemon prio=10 tid=3 Runnable
          native: #00 pc 000000000009a0c8  /apex/com.android.runtime/lib64/bionic/libc.so (syscall+24)

        ----- end 4242 -----
        """;

    [Fact]
    public void AnAndroidAnr_ReadsTheMainThreadAndWhatItWaitsOn()
    {
        var text = CrashReportText.Summarize("traces.txt", Anr, 8000)!;
        Assert.Contains("Cmd line: com.example.shop", text);
        Assert.Contains("\"main\" prio=5 tid=1 Blocked", text);
        Assert.Contains("- waiting to lock <0x0a1b2c3d> (a java.lang.Object) held by thread 14", text);
        Assert.DoesNotContain("sCount", text);                             // the detail lines
        Assert.Contains("\"Sync\" prio=5 tid=14 Sleeping", text);            // the holder of the lock main waits on
        Assert.Contains("- locked <0x0a1b2c3d>", text);
        Assert.Contains("1 other threads of this process", text);
    }

    [Fact]
    public void ALogThatQuotesACrash_AndAnyOtherFile_AreReadAsTheirLines()
    {
        Assert.Null(CrashReportText.Summarize("app.log", Crash, 8000));
        Assert.Null(CrashReportText.Summarize("notes.txt", Anr, 8000));
        Assert.Null(CrashReportText.Summarize("tombstone_notes.md", "nothing crashed here", 8000));
        Assert.Null(CrashReportText.Summarize("report.wer", "a note", 8000));
    }

    [Fact]
    public void AReportPastItsBudget_IsCut_AndSaysHowToReadTheRest()
    {
        var text = CrashReportText.Summarize("Shop.crash", Crash, 260)!;
        Assert.Contains("Cut to fit", text);
        Assert.True(text.Length <= 260);
    }

    [Fact]
    public async Task ReadFile_DecodesTheWindowsReport_AndGivesItsLinesOnRequest()
    {
        // Through the tool: the UTF-16 file decoded by its byte order mark, the summary first, its lines when asked.
        var dir = Directory.CreateTempSubdirectory("inferpal-crash-test-").FullName;
        try
        {
            File.Copy(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "crash", "Report.wer"),
                      Path.Combine(dir, "Report.wer"));
            var tool = new ReadFileTool(() => dir);
            var first = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = "Report.wer" }), CancellationToken.None);
            Assert.Contains("Windows Error Report Report.wer: APPCRASH", first);
            var lines = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = "Report.wer", start_line = 1, end_line = 2 }), CancellationToken.None);
            Assert.Contains("EventType=APPCRASH", lines);
            Assert.DoesNotContain("Windows Error Report", lines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void ATombstoneInAnAndroidBugReport_IsRecognisedByItsOwnName() =>
        Assert.NotNull(CrashReportText.Summarize("FS/data/tombstones/tombstone_03", Tombstone, 8000));

    [Fact]
    public void AWatchdogTermination_SaysItsReason_AndNoMarkedThreadIsNamed()
    {
        const string ips = """
            {"app_name":"Shop","bug_type":"309","os_version":"iPhone OS 18.1 (22B83)"}
            { "procName" : "Shop", "exception" : { "type" : "EXC_CRASH", "signal" : "SIGKILL" },
              "termination" : { "namespace" : "FRONTBOARD", "code" : 2343432205,
                                "reasons" : ["<RBSTerminateContext| code:0x8BADF00D explanation:scene-update watchdog transgression>"] },
              "threads" : [ { "frames" : [] }, { "frames" : [] } ], "usedImages" : [] }
            """;
        var text = CrashReportText.Summarize("Shop.ips", ips, 8000)!;
        Assert.Contains("Termination: FRONTBOARD 2343432205 — <RBSTerminateContext| code:0x8BADF00D", text);
        Assert.Contains("No thread is marked as the one that crashed.", text);
        Assert.DoesNotContain("Thread 0 crashed", text);
    }
}
