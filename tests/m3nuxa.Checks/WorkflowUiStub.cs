namespace m3nuxa.App;

// These tests do not exercise file operations or UI. Fail immediately if one is invoked.
internal sealed class FileOperationProgressDialog : IDisposable
{
    public FileOperationProgressDialog(int count) => throw new NotSupportedException("UI is unavailable in workflow checks.");
    public event EventHandler? CancelRequested { add { } remove { } }
    public void Activate() => throw new NotSupportedException();
    public void Report(int completed, string path) => throw new NotSupportedException();
    public Task<FileConflictChoice> AskConflict(string path, CancellationToken token) => throw new NotSupportedException();
    public void Dispose() { }
}
