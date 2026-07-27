namespace Inferpal.Services.CodeActions;

internal record DiffInfo(string OldText, string NewText, string FilePath = "");
