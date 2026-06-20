namespace Inferpal.Services;

internal record DiffInfo(string OldText, string NewText, string FilePath = "");
