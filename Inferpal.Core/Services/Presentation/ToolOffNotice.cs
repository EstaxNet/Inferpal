using System.Text.RegularExpressions;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Presentation;

/// <summary>What the tool-off notice says: the family a question calls for, its tools, and its label.</summary>
/// <param name="Key">The family, as it is said once per conversation.</param>
/// <param name="Label">Its name, in the interface's language.</param>
/// <param name="Tools">The tools the gestures open or turn back on.</param>
/// <param name="Text">The notice, in the interface's language.</param>
internal sealed record ToolOffFinding(string Key, string Label, IReadOnlyList<string> Tools, string Text);

/// <summary>
/// The tool-off notice (pari 163): a question whose words call for a family of tools the person turned off is told so,
/// with a way to use them for this question or turn them back on. Said once per family and conversation, by both editors
/// from this one reader.
/// </summary>
/// <remarks>
/// ⚠ Judged on the question as typed, never the active file or the attachments, which quote code. ⚠ Only when EVERY
/// built-in tool of the family that this editor serves is off by the person: one left on, and the agent has what it
/// needs. The words are the person's, in the interface's ten languages; a false positive costs one notice, a miss is
/// today's silence.
/// </remarks>
internal static class ToolOffNotice
{
    private sealed record Family(string Key, string LabelKey, string[] Tools, Regex Words);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static Regex Words(string pattern) => new(pattern, Options, RegexBudget.Default);

    private static readonly Family[] Families =
    [
        new("web", "AssistInternet", ["web_search", "fetch_url"], Words(
            @"\brelease notes?\b|\bchangelog of\b|\b(search|look)\b.{0,20}\b(the web|online|the internet)\b|\blatest version of\b|\bon the internet\b"
            + @"|notes de (version|publication)|\b(cherche|recherche)\w*\b.{0,20}\b(sur le web|sur internet|en ligne)|derni[èe]re version de"
            + @"|versionshinweise|\bim (web|internet)\b|neueste version"
            + @"|notas de (la )?versi[oó]n|\bbusca\w*\b.{0,20}\b(en la web|en internet)|[uú]ltima versi[oó]n de"
            + @"|note di rilascio|\bcerca\w*\b.{0,20}\b(sul web|in rete|su internet)|ultima versione di"
            + @"|примечани\w* к выпуску|в интернете|последн\w* верси"
            + @"|リリースノート|ネットで|ウェブで|最新バージョン"
            + @"|릴리스 노트|웹에서|인터넷에서|최신 버전"
            + @"|informacje o wydaniu|w (sieci|internecie)|najnowsz\w* wersj"
            + @"|发布说明|更新日志|网上|最新版本")),
        new("debugger", "AssistDebugger", [.. ToolsAssistant.Debugger], Words(
            @"\bdebug(ger|ging)?\b|\bbreakpoints?\b|\bstep (through|into)\b"
            + @"|d[ée]bog|point d.arr[êe]t|pas [àa] pas"
            + @"|debugg|haltepunkt|einzelschritt"
            + @"|depura|punto de interrupci"
            + @"|punto di interruzione|passo passo"
            + @"|отлад|точк\w* останова|пошагов"
            + @"|デバッグ|ブレークポイント|ステップ実行"
            + @"|디버그|디버깅|중단점|브레이크포인트"
            + @"|punkt\w* przerwania|krok po kroku"
            + @"|调试|断点|单步")),
        new("pages", "AssistWorkWebPages", ["view_page", "interact_page"], Words(
            @"\bin the browser\b|\bthe (web )?page (shows|looks|displays|renders)\b|\bscreenshot of the (page|site)\b|\bclick (on )?the\b.{0,30}\bbutton\b"
            + @"|dans le navigateur|la page (affiche|s.affiche)|capture de la page|\bclique\w*\b.{0,20}\bbouton\b"
            + @"|im browser|die seite zeigt|\bklick\w*\b.{0,30}\b(button|schaltfl[äa]che)"
            + @"|en el navegador|la p[aá]gina (muestra|se ve)|haz clic en el bot[oó]n"
            + @"|nel browser|la pagina (mostra|appare)|clicca sul pulsante"
            + @"|в браузере|нажми на кнопку"
            + @"|ブラウザで|ボタンをクリック"
            + @"|브라우저에서|버튼을 클릭"
            + @"|w przegl[aą]darce|kliknij przycisk"
            + @"|浏览器|点击按钮")),
        new("desktop", "AssistWorkDesktopApps", ["view_window", "interact_window"], Words(
            @"\b(wpf|winforms|windows forms|avalonia)\b|\bdialog box\b|\bdesktop app"
            + @"|bo[îi]te de dialogue|appli(cation)? de bureau"
            + @"|desktop-app|dialogfenster"
            + @"|aplicaci[oó]n de escritorio|cuadro de di[aá]logo"
            + @"|applicazione desktop|finestra di dialogo"
            + @"|настольн\w* приложени|диалогов\w* окн"
            + @"|デスクトップアプリ|ダイアログ"
            + @"|데스크톱 앱|대화 상자"
            + @"|aplikacj\w* desktopow|okn\w* dialogow"
            + @"|桌面应用|对话框")),
        new("data", "AssistWorkData", ["query_database", "query_data"], Words(
            @"\b(in|from|into|query|on) (the|my|our) (database|db)\b|\bsql (query|queries)\b|\.(csv|xlsx|parquet)\b"
            + @"|dans la base de donn[ée]es|requ[êe]te sql"
            + @"|in der datenbank|sql-abfrage"
            + @"|en la base de datos|consulta sql"
            + @"|nel database|query sql"
            + @"|в базе данных|sql-запрос"
            + @"|データベース(の|から|に)|sqlクエリ"
            + @"|데이터베이스(에서|의)|sql 쿼리"
            + @"|w bazie danych|zapytani\w* sql"
            + @"|数据库(中|里)")),
        new("api", "AssistWorkApis", ["http_request"], Words(
            @"\bcall (the|my|our) (api|endpoint)\b|\bsend an? (request|get|post)\b|\b(get|post|put|patch|delete) /\w"
            + @"|appel\w* l.api|envoi\w* une requ[êe]te"
            + @"|api aufrufen|anfrage senden"
            + @"|llama\w* a la api|env[ií]a\w* una petici"
            + @"|chiama\w* l.api|invia\w* una richiesta"
            + @"|вызов\w* api|отправ\w* запрос"
            + @"|apiを呼|リクエストを送"
            + @"|api를 호출|요청을 보내"
            + @"|wywo[lł]aj api|wy[sś]lij [żz][aą]danie"
            + @"|调用\s?api|发送请求")),
        new("refactoring", "AssistWorkRefactoring", ["rename_symbol", "analyze_code"], Words(
            @"\brename\b.{0,40}\b(everywhere|across)\b|\bwho calls\b|\bcallers of\b|\bwhat (calls|uses)\b|\bimpact of changing\b"
            + @"|renomm\w*\b.{0,40}\bpartout|qui appelle|les appelants|impact d.un changement"
            + @"|[üu]berall umbenennen|wer ruft|aufrufer"
            + @"|renombra\w*\b.{0,40}\ben todas partes|qui[eé]n llama"
            + @"|rinomina\w*\b.{0,40}\bovunque|chi chiama"
            + @"|переимен\w*\b.{0,40}\bвезде|кто вызывает"
            + @"|呼び出し元|どこから呼ばれ"
            + @"|호출하는 곳|호출자"
            + @"|zmie[nń] nazw\w*\b.{0,40}\bwsz[eę]dzie|kto wywo[lł]uje"
            + @"|谁调用|调用方")),
        new("documents", "AssistWorkDocuments", ["edit_notebook", "run_notebook", "ocr"], Words(
            @"\.ipynb\b|\bnotebook\b|\bocr\b|\bscanned\b|\btext in (the|this) (image|picture|screenshot)\b"
            + @"|texte de l.image|scann[ée]"
            + @"|gescannt|text im bild"
            + @"|cuaderno|escanead|texto de la imagen"
            + @"|scansionat|testo dell.immagine"
            + @"|блокнот|отсканир|текст на (картинке|изображении)"
            + @"|ノートブック|スキャンした(画像|書類|文書)|画像の文字"
            + @"|주피터 노트북|스캔한 (이미지|문서)|이미지의 텍스트"
            + @"|notatnik|zeskanowan|tekst na obrazie"
            + @"|jupyter 笔记本|扫描的(图片|文档)|图片中的文字")),
        new("tests", "AssistWorkTests", ["run_tests", "get_diagnostics"], Words(
            @"\brun (the )?(unit )?tests?\b|\btests? (fail|fails|failing|pass)\b|\bdoes it (build|compile)\b"
            + @"|lance\w* les tests|les tests ([ée]chouent|passent)|[çc]a compile"
            + @"|tests? (ausf[üu]hren|laufen)|kompiliert es"
            + @"|ejecuta\w* (los )?tests|las pruebas (fallan|pasan)"
            + @"|esegui\w* i test|i test (falliscono|passano)"
            + @"|запусти\w* тест|тесты (падают|проходят)"
            + @"|テストを(実行|走らせ)|テストが(失敗|通)"
            + @"|테스트를 실행|테스트가 (실패|통과)"
            + @"|uruchom\w* testy|testy (nie )?przechodz"
            + @"|运行测试|测试(失败|通过)")),
        new("commands", "AssistCommands", ["run_command"], Words(
            @"\b(npm|pip|dotnet|cargo) (install|add|run)\b|\brun (this|the|that) (command|script|program)\b|\bin the terminal\b"
            + @"|lance\w* (la commande|le script|le programme)|dans le terminal"
            + @"|befehl ausf[üu]hren|im terminal"
            + @"|ejecuta\w* (el comando|el script)|en la terminal"
            + @"|esegui\w* (il comando|lo script)|nel terminale"
            + @"|выполни\w* команд|в терминале"
            + @"|コマンドを実行|ターミナルで"
            + @"|명령을 실행|터미널에서"
            + @"|uruchom\w* (polecenie|skrypt)|w terminalu"
            + @"|运行命令|在终端")),
    ];

    /// <summary>The families the notice knows, by key — for a test that walks them.</summary>
    internal static IReadOnlyList<string> Keys => [.. Families.Select(f => f.Key)];

    /// <summary>
    /// The family <paramref name="question"/> calls for whose served built-in tools are all turned off by the person, not
    /// yet said in this conversation (<paramref name="said"/>, which this adds it to) — <c>null</c> otherwise.
    /// </summary>
    internal static ToolOffFinding? For(string? question, IReadOnlyList<ToolProfileEntry> profile, ISet<string> said)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        foreach (var family in Families)
        {
            if (said.Contains(Key(family.Key))) continue;
            var served = profile.Where(e => e.Source == ToolSource.BuiltIn && family.Tools.Contains(e.Key)).ToList();
            if (served.Count == 0 || served.Any(e => e.State != ToolState.OffByUser)) continue;
            bool matches;
            try { matches = family.Words.IsMatch(question); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!matches) continue;
            said.Add(Key(family.Key));
            var tools = served.Select(e => e.Key).ToList();
            var label = Localization.Strings.ByName(family.LabelKey);
            return new(family.Key, label, tools, Localization.Strings.ToolOffNoticeText(label, string.Join(", ", tools)));
        }
        return null;
    }

    /// <summary>Whether <paramref name="question"/> calls for the family <paramref name="key"/> — the words alone.</summary>
    internal static bool Calls(string key, string question) => Families.First(f => f.Key == key).Words.IsMatch(question);

    private static string Key(string family) => "tooloff|" + family;

    /// <summary>Forgets the families <paramref name="said"/> holds: a new conversation is told again.</summary>
    internal static void Forget(ISet<string> said)
    {
        foreach (var k in said.Where(k => k.StartsWith("tooloff|", StringComparison.Ordinal)).ToList()) said.Remove(k);
    }
}
