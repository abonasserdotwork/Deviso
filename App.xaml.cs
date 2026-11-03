using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows;
using System.Windows.Shell;
using MessageBox = System.Windows.MessageBox;
using Deviso.Services;

namespace Deviso;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _pipeCancellation = new();
    private string _pipeName = "";
    private Task? _pipeTask;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _pipeName = GetPipeName();
        var command = e.Args.FirstOrDefault(argument => argument.StartsWith("--task=", StringComparison.Ordinal))
            ?.Substring("--task=".Length);
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            name: GetMutexName(),
            createdNew: out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            var forwardedCommand = command ?? TaskbarCommand.Open;
            if (!await TrySendCommandAsync(forwardedCommand) &&
                forwardedCommand != TaskbarCommand.Exit)
            {
                MessageBox.Show(
                    "The running Deviso window could not be reached. Try again in a moment.",
                    "Deviso",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        if (command == TaskbarCommand.Exit)
        {
            Shutdown();
            return;
        }

        try
        {
            WindowsTaskbarIdentity.SetCurrentProcessAppUserModelId();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Windows could not register Deviso's taskbar identity. Right-click taskbar actions may be unavailable.\n\n{exception.Message}",
                "Deviso — taskbar setup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        try
        {
            new StartMenuShortcutService().RefreshIfInstalled();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Could not update Deviso's Start menu shortcut for taskbar integration.\n\n{exception.Message}",
                "Deviso — shortcut update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ConfigureJumpList();
        var window = new MainWindow();
        MainWindow = window;
        _pipeTask = ListenForCommandsAsync(_pipeCancellation.Token);
        window.Show();
        if (command == TaskbarCommand.Reports)
        {
            _ = window.Dispatcher.BeginInvoke(() => window.HandleTaskbarCommand(command));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCancellation.Cancel();
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ConfigureJumpList()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var jumpList = new JumpList
        {
            ShowRecentCategory = false,
            ShowFrequentCategory = false
        };
        jumpList.JumpItems.Add(CreateTask("Open Deviso", "open", executable));
        jumpList.JumpItems.Add(CreateTask("Refresh drives", "refresh", executable));
        jumpList.JumpItems.Add(CreateTask("Open reports & history", "reports", executable));
        jumpList.JumpItems.Add(CreateTask("Exit Deviso", "exit", executable));
        try
        {
            JumpList.SetJumpList(this, jumpList);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Windows could not register Deviso's taskbar actions.\n\n{exception.Message}",
                "Deviso — taskbar actions",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static JumpTask CreateTask(string title, string command, string executable) => new()
    {
        Title = title,
        Description = title,
        ApplicationPath = executable,
        Arguments = $"--task={command}",
        IconResourcePath = executable,
        IconResourceIndex = 0
    };

    private async Task<bool> TrySendCommandAsync(string command)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync(command);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task ListenForCommandsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (command is not null && MainWindow is Deviso.MainWindow window)
                {
                    _ = window.Dispatcher.BeginInvoke(() => window.HandleTaskbarCommand(command));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private static string GetPipeName()
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? Environment.UserName;
        return $"Deviso-{userSid.Replace('\\', '_')}";
    }

    private static string GetMutexName() =>
        $"Local\\{GetPipeName()}";
}

internal static class TaskbarCommand
{
    public const string Open = "open";
    public const string Refresh = "refresh";
    public const string Reports = "reports";
    public const string Exit = "exit";
}
