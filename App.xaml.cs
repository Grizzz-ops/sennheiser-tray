using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SennheiserTray
{
    public partial class App : System.Windows.Application
    {
        private static readonly string UserSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        private static string MutexName => $@"Local\SennheiserTray.{UserSid}.SingleInstance";
        private static string PipeName => $"SennheiserTray.SingleInstance.{UserSid}.Activate";
        private Mutex? _instanceMutex;
        private CancellationTokenSource? _pipeCancellation;
        private MainWindow? _mainWindow;
        private bool _ownsMutex;
        private bool _restartRequested;
        private DateTimeOffset _lastDispatcherErrorDialogAt = DateTimeOffset.MinValue;

        public void RequestRestart()
        {
            _restartRequested = true;
            Shutdown();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppLogger.Initialize();
            RegisterGlobalExceptionHandlers();
            base.OnStartup(e);

            bool createdNew;
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
            if (!createdNew)
            {
                SignalExistingInstance();
                Shutdown();
                return;
            }
            _ownsMutex = true;

            _pipeCancellation = new CancellationTokenSource();
            _ = Task.Run(() => ListenForActivationAsync(_pipeCancellation.Token));
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            _mainWindow.Show();
        }

        private void RegisterGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                if (args.ExceptionObject is Exception exception)
                {
                    AppLogger.LogException("AppDomain unhandled exception", exception);
                }
                else
                {
                    AppLogger.LogException(
                        "AppDomain unhandled exception",
                        new InvalidOperationException($"Non-exception object: {args.ExceptionObject}"));
                }
            };

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                AppLogger.LogException("Unobserved task exception", args.Exception);
                args.SetObserved();
            };

            DispatcherUnhandledException += (sender, args) =>
            {
                AppLogger.LogException("Dispatcher unhandled exception", args.Exception);
                if (DateTimeOffset.UtcNow - _lastDispatcherErrorDialogAt >= TimeSpan.FromMinutes(1))
                {
                    _lastDispatcherErrorDialogAt = DateTimeOffset.UtcNow;
                    System.Windows.MessageBox.Show(
                        $"The error was logged locally. {args.Exception.Message}",
                        "Sennheiser Control Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                args.Handled = true;
            };
        }

        private static void SignalExistingInstance()
        {
            Exception? lastError = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                    client.Connect(250);
                    client.WriteByte(1);
                    client.Flush();
                    return;
                }
                catch (Exception ex) when (
                    ex is IOException
                    || ex is TimeoutException
                    || ex is UnauthorizedAccessException)
                {
                    lastError = ex;
                    Thread.Sleep(100);
                }
            }

            AppLogger.LogException(
                "Could not activate existing instance",
                lastError ?? new IOException("The activation pipe was unavailable."));
        }

        private async Task ListenForActivationAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(cancellationToken);
                    byte[] signal = new byte[1];
                    int bytesRead = await server.ReadAsync(signal.AsMemory(), cancellationToken);
                    if (bytesRead == 1 && signal[0] == 1)
                    {
                        await Dispatcher.InvokeAsync(ShowMainWindow);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Single-instance activation listener failed", ex);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                return;
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.Focus();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _pipeCancellation?.Cancel();
            _pipeCancellation?.Dispose();
            if (_instanceMutex != null && _ownsMutex)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    AppLogger.LogException("Could not release single-instance mutex", ex);
                }

            }

            _instanceMutex?.Dispose();
            _instanceMutex = null;
            base.OnExit(e);

            if (_restartRequested)
            {
                try
                {
                    string executablePath = Environment.ProcessPath
                        ?? throw new InvalidOperationException("The application executable path is unavailable.");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executablePath,
                        WorkingDirectory = AppContext.BaseDirectory,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    AppLogger.LogException("Could not restart Sennheiser Control", ex);
                }
            }
        }
    }
}
