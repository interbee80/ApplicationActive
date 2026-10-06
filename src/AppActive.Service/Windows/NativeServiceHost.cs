using System.ComponentModel;
using System.Runtime.InteropServices;
using AppActive.Core;
using Microsoft.Extensions.Hosting;

namespace AppActive.Service.Windows;

/// <summary>
/// Хост службы Windows без внешних зависимостей: регистрирует точку входа ServiceMain,
/// держит статус в SCM, запускает ASP.NET Core host и корректно останавливает его.
/// </summary>
internal static class NativeServiceHost
{
    private static WebApplication? _app;
    private static CancellationTokenSource? _cts;
    private static Task? _appTask;
    private static IntPtr _statusHandle = IntPtr.Zero;
    private static ServiceInterop.ServiceMainCallback? _mainCallback;
    private static ServiceInterop.ServiceControlHandlerEx? _handlerCallback;
    private static readonly ManualResetEventSlim Stopped = new(false);
    private static string _serviceName = "AppActive";
    private static int _exitCode;
    private static uint _currentState = ServiceInterop.SERVICE_START_PENDING;

    /// <summary>
    /// Пытается подключиться к SCM. Возвращает false, если процесс запущен не как служба
    /// (тогда вызывающий код работает в обычном консольном режиме).
    /// </summary>
    public static bool TryStartDispatcher(string serviceName, WebApplication app, out int exitCode)
    {
        _serviceName = serviceName;
        _app = app;
        _mainCallback = ServiceMain;
        _handlerCallback = HandleControl;

        var table = new ServiceInterop.SERVICE_TABLE_ENTRY[2];
        table[0].lpServiceName = serviceName;
        table[0].lpServiceProc = _mainCallback;
        table[1].lpServiceName = null;
        table[1].lpServiceProc = null;

        exitCode = 0;
        if (ServiceInterop.StartServiceCtrlDispatcher(table))
        {
            exitCode = _exitCode;
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ServiceInterop.ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
        {
            return false;
        }

        throw new Win32Exception(error, "Не удалось подключиться к диспетчеру служб (StartServiceCtrlDispatcher)");
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handlerCallback ??= HandleControl;
        _statusHandle = ServiceInterop.RegisterServiceCtrlHandlerEx(_serviceName, _handlerCallback, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            AppLog.Error($"Не удалось зарегистрировать обработчик службы: {Marshal.GetLastWin32Error()}");
            return;
        }

        ReportStatus(ServiceInterop.SERVICE_START_PENDING, 10000);
        AppLog.Info($"Служба '{_serviceName}' запускается как служба Windows");

        _cts = new CancellationTokenSource();
        var app = _app!;
        _appTask = Task.Run(async () =>
        {
            try
            {
                await ((IHost)app).StartAsync(_cts.Token);
                ReportStatus(ServiceInterop.SERVICE_RUNNING, 0);
                await ((IHost)app).WaitForShutdownAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // штатная остановка
            }
            catch (Exception ex)
            {
                AppLog.Error($"Служба остановлена из-за ошибки: {ex.Message}", ex);
                _exitCode = 1;
            }
            finally
            {
                try
                {
                    await ((IHost)app).StopAsync(TimeSpan.FromSeconds(20));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Ошибка при остановке хоста: {ex.Message}");
                }
            }
        });

        Stopped.Wait();
        ReportStatus(ServiceInterop.SERVICE_STOPPED, 0);
        AppLog.Info($"Служба '{_serviceName}' остановлена");
    }

    private static uint HandleControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ServiceInterop.SERVICE_CONTROL_STOP:
            case ServiceInterop.SERVICE_CONTROL_SHUTDOWN:
                ReportStatus(ServiceInterop.SERVICE_STOP_PENDING, 30000);
                AppLog.Info($"Получена команда остановки службы (control={control})");
                try
                {
                    _cts?.Cancel();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Ошибка при отмене токена остановки: {ex.Message}");
                }

                try
                {
                    _appTask?.Wait(TimeSpan.FromSeconds(25));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Хост не завершился за 25 с: {ex.Message}");
                }
                finally
                {
                    Stopped.Set();
                }
                return 0;

            case ServiceInterop.SERVICE_CONTROL_INTERROGATE:
                ReportStatus(_currentState, 0);
                return 0;

            default:
                return 0;
        }
    }

    private static void ReportStatus(uint state, uint waitHint)
    {
        _currentState = state;
        if (_statusHandle == IntPtr.Zero) return;
        var status = new ServiceInterop.SERVICE_STATUS
        {
            dwServiceType = ServiceInterop.SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = state == ServiceInterop.SERVICE_RUNNING
                ? ServiceInterop.SERVICE_ACCEPT_STOP | ServiceInterop.SERVICE_ACCEPT_SHUTDOWN
                : 0,
            dwWin32ExitCode = 0,
            dwServiceSpecificExitCode = 0,
            dwCheckPoint = 0,
            dwWaitHint = waitHint
        };
        ServiceInterop.SetServiceStatus(_statusHandle, ref status);
    }
}
