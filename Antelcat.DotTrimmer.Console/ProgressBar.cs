namespace Antelcat.DotTrimmer;

public class ProgressBar : IDisposable
{
    public int Current
    {
        get => current;
        set
        {
            current = value;
            progress = current * 100 / total;
        }
    }

    private int current, progress;
    private readonly string prompt;
    private readonly int total;
    private readonly CancellationTokenSource cancellationTokenSource = new();

    public ProgressBar(string prompt, int total)
    {
        this.prompt = prompt;
        this.total = total;
        Task.Factory.StartNew(UpdateTask, cancellationTokenSource.Token, TaskCreationOptions.LongRunning);
    }

    private async ValueTask UpdateTask(object? arg)
    {
        var cancellationToken = (CancellationToken)arg!;
        var progressStrings = new[]
        {
            " - ",
            " \\ ",
            " | ",
            " / "
        };
        var progressStringIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.CursorVisible = false;
            Console.CursorLeft = 0;
            Console.Write(progressStrings[progressStringIndex++]);
            if (progressStringIndex == progressStrings.Length) progressStringIndex = 0;
            Console.Write(prompt);
            Console.Write(progress);
            Console.Write('%');

            await Task.Delay(100, cancellationToken);
        }
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        Console.CursorLeft = 0;
        Console.WriteLine($"{prompt}OK!    ");
        Console.CursorVisible = true;
    }
}