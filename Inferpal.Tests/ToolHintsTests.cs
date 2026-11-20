using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A parameter models do not find (run_command's repeat, read_file's level and time filter) is said with the question
//  that calls for it — the questions of the pari 125 gate, in the ten languages — with no other, and only for a tool the
//  turn sends.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class ToolHintsTests
{
    private static bool All(string _) => true;

    [Theory]
    // The questions the hint was measured on.
    [InlineData("Rends ce rapport plus rapide.")]
    [InlineData("Le programme Report est trop lent sur orders.csv, accélère-le.")]
    [InlineData("Make the Report program faster.")]
    [InlineData("Optimise Report/Program.cs : il met trop de temps sur un gros fichier.")]
    [InlineData("The sales report takes too long to run. Can you speed it up?")]
    // The other languages of the interface.
    [InlineData("Mach den Bericht schneller.")]
    [InlineData("Haz que el informe sea más rápido.")]
    [InlineData("Rendi il report più veloce.")]
    [InlineData("Ottimizza il report.")]
    [InlineData("Ускорь этот отчёт.")]
    [InlineData("このレポートを速くして。")]
    [InlineData("このレポートは遅すぎる。")]
    [InlineData("이 보고서를 더 빠르게 만들어 줘.")]
    [InlineData("보고서가 너무 느려요.")]
    [InlineData("Przyspiesz ten raport.")]
    [InlineData("让这个报告更快。")]
    [InlineData("这个报告很慢。")]
    public void AQuestionForSpeed_CarriesTheMeasure(string question) =>
        Assert.Equal(ToolHints.Header + "\n\n" + ToolHints.Measure, ToolHints.For(question, All));

    /// <summary>A duration to measure, not only a speed to gain: without the hint, the model times the program once, by
    /// hand.</summary>
    [Theory]
    [InlineData("How long does the nightly job in OrderLoop take to run? Measure it reliably.")]
    [InlineData("Benchmark the export.")]
    [InlineData("Measure the execution time of Report.")]
    [InlineData("Combien de temps prend le job de nuit ?")]
    [InlineData("Mesure le temps d'exécution de Report.")]
    [InlineData("Wie lange braucht der Export?")]
    [InlineData("¿Cuánto tarda el informe?")]
    [InlineData("Quanto tempo ci vuole per il report?")]
    [InlineData("Сколько времени занимает отчёт?")]
    [InlineData("このレポートの実行時間を測って。")]
    [InlineData("보고서 실행 시간을 측정해 줘.")]
    [InlineData("Ile czasu trwa raport?")]
    [InlineData("How fast does the export run?")]
    [InlineData("Combien dure l'export ?")]
    [InlineData("¿Cuánto dura el informe?")]
    [InlineData("Quanto dura l'export?")]
    [InlineData("Wie viel Zeit braucht der Export?")]
    [InlineData("この処理はどれだけ時間がかかる？")]
    [InlineData("How long does the nightly job\ntake to run?")]                // a pasted question breaks lines anywhere
    [InlineData("这个报告运行时间是多少？")]
    public void AQuestionForADuration_CarriesTheMeasure(string question) =>
        Assert.Equal(ToolHints.Header + "\n\n" + ToolHints.Measure, ToolHints.For(question, All));

    [Theory]
    [InlineData("Quelles erreurs depuis 10 h 30 ?")]
    [InlineData("Y a-t-il eu des erreurs dans app.log depuis 10h30 ?")]
    [InlineData("What errors has the app logged since 10:30?")]
    [InlineData("Le service a eu des soucis ce matin : qu'est-ce qui a échoué après 10 h 30 ?")]
    [InlineData("Check the log for any errors after 10:30 and tell me what caused them.")]
    [InlineData("What failed after 10:30:00?")]
    [InlineData("Any errors since 10am?")]
    [InlineData("Welche Fehler gab es seit 10.30 Uhr?")]
    [InlineData("10時30分以降のエラーは？")]
    [InlineData("10:30 以后日志里有什么错误？")]
    public void AQuestionForALogsErrorsByTime_CarriesTheFilter(string question) =>
        Assert.Equal(ToolHints.Header + "\n\n" + ToolHints.LogFilter, ToolHints.For(question, All));

    [Theory]
    [InlineData("Add a Reset button to the cart page, with its test.")]
    [InlineData("Rename Total to GrandTotal everywhere.")]
    [InlineData("Fix the errors since my last commit.")]                  // a build, not a log: no time, no log named
    [InlineData("Explique ce que fait OrderExporter.")]
    [InlineData("Can you quickly add a null check?")]                     // in a hurry is not about speed
    [InlineData("Implement optimistic concurrency on Order.")]
    [InlineData("Cette méthode est trop longue, découpe-la.")]
    [InlineData("Add a keyboard accelerator to the Save button.")]
    [InlineData("Fix the runtime error in Report.")]                      // the runtime, not a duration
    [InlineData("Measure the width of the panel.")]                       // a measure, no time
    [InlineData("Behebe den Laufzeitfehler.")]
    [InlineData("Every time it starts, show the splash screen.")]          // "time it", no measure
    [InlineData("Fix the Benchmark project's build.")]                     // a project's name
    [InlineData("Return a 504 when the response time exceeds 5 s.")]      // a timeout to set
    [InlineData("このプログラムのエラーを直して。")]                       // ログ inside プログラム
    [InlineData("프로그램 오류를 고쳐 줘.")]                               // 로그 inside 프로그램
    [InlineData("Ошибка в диалоге настроек.")]                            // лог inside диалог
    [InlineData("src/app.ts:12:34 - error TS2345: Argument of type 'string'.")]   // a position in pasted output
    [InlineData("Zwolnij zasoby w Dispose.")]
    [InlineData("ダイアログのエラーを直して。")]                          // ダイアログ, a dialog
    [InlineData("블로그 페이지 오류를 고쳐 줘.")]                          // 블로그, a blog
    [InlineData("Error en el registro de usuarios.")]                      // a sign-up
    [InlineData("Users get an error when they log in.")]                   // the verb
    [InlineData("빠르게 null 체크를 추가해 줘.")]                          // quickly, not faster
    [InlineData("速く null チェックを追加して。")]
    [InlineData("Не надо оптимизма, исправь тест.")]                       // оптимизм
    [InlineData("Error at line 12: 34 is not a valid value.")]
    [InlineData("")]
    public void AnyOtherQuestion_CarriesNone(string question) => Assert.Null(ToolHints.For(question, All));

    [Fact]
    public void BothAsked_BothSaid_AndATurnWithoutTheTool_SaysNothingOfIt()
    {
        const string question = "The app got slow after 10:30: what errors are in app.log, and can you make it faster?";

        Assert.Equal(ToolHints.Header + "\n\n" + ToolHints.Measure + "\n\n" + ToolHints.LogFilter, ToolHints.For(question, All));
        Assert.Equal(ToolHints.Header + "\n\n" + ToolHints.LogFilter, ToolHints.For(question, name => name != "run_command"));
        Assert.Null(ToolHints.For(question, _ => false));
    }

    private sealed class Registry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [
            new("function", new ToolFunction("read_file", "Read a file.", new { type = "object" })),
            new("function", new ToolFunction("run_command", "Run a command.", new { type = "object" })),
        ];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ok");
    }

    [Fact]
    public void TheToolsOffered_AreTheOnesTheTurnSends()
    {
        var tools = new Registry();
        var readOnly = new ChatMode("Ask", "", "", [], ModeTools.FromRoo([("read", null, null, false)]), null,
                                    ".inferpal/modes/ask.md", ChatModeOrigin.Inferpal, []);

        Assert.True(ToolHints.OfferedBy(tools, toolsEnabled: true, planMode: false, () => null)("run_command"));
        Assert.False(ToolHints.OfferedBy(tools, toolsEnabled: false, planMode: false, () => null)("read_file"));  // a code action
        Assert.False(ToolHints.OfferedBy(tools, toolsEnabled: true, planMode: true, () => null)("run_command"));
        Assert.True(ToolHints.OfferedBy(tools, toolsEnabled: true, planMode: true, () => null)("read_file"));
        Assert.False(ToolHints.OfferedBy(tools, toolsEnabled: true, planMode: false, () => readOnly)("run_command"));
        Assert.True(ToolHints.OfferedBy(tools, toolsEnabled: true, planMode: false, () => readOnly)("read_file"));
    }

    [Fact]
    public void TheTurnsTools_AreReadOnlyWhenAQuestionCallsForAHint()
    {
        var read = 0;
        var offered = ToolHints.OfferedBy(new Registry(), toolsEnabled: true, planMode: false, () => { read++; return null; });

        Assert.Null(ToolHints.For("hello", offered));
        Assert.Equal(0, read);                                    // most questions never pay for the registry
        Assert.NotNull(ToolHints.For("Make it faster.", offered));
        Assert.Equal(1, read);
    }
}
