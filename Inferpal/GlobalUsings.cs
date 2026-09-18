// Sub-namespaces of Services/, split by responsibility. Exposed globally to avoid adding a `using`
// per consuming file: the types all lived in Inferpal.Services, a single namespace, so splitting
// them up cannot create a name conflict.
global using Inferpal.Services.Inference;
global using Inferpal.Services.Agent;
global using Inferpal.Services.Execution;
global using Inferpal.Services.Hardware;
global using Inferpal.Services.CodeActions;
global using Inferpal.Services.Prompting;
global using Inferpal.Services.Persistence;
global using Inferpal.Services.Governance;
global using Inferpal.Services.VsIntegration;
global using Inferpal.Services.Presentation;
global using Inferpal.Services.Editor;
global using Inferpal.Services.Signals;
global using Inferpal.Services.Tasks;
