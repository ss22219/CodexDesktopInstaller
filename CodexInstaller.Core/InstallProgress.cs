namespace CodexInstaller.Core;

public class InstallProgress
{
    public int Percent { get; set; }
    public string Status { get; set; } = "";
    public bool IsCompleted { get; set; }
    public string? ErrorMessage { get; set; }
}
