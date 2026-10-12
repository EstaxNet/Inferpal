// Entry of the browser settings window (`Inferpal.Host --settings`): the shell first — it defines acquireVsCodeApi,
// which the form calls as it loads — then the very form VS Code shows.
import './settingsStandaloneShell';
import './settings';
