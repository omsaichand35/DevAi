namespace CollabServer.Models;

public class FileEvent
{
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
}

public class FileRenameEvent
{
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
}
