// Sous-namespaces de Services/ réorganisés par responsabilité (juin 2026).
// Exposés globalement pour éviter d'ajouter un `using` par fichier consommateur :
// les types vivaient tous dans Inferpal.Services (un seul namespace), donc aucun
// conflit de nom ne peut apparaître en les répartissant.
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
