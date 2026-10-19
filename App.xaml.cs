using System.Windows;

namespace ImageViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var arg = e.Args.FirstOrDefault();
        switch (arg?.ToLowerInvariant())
        {
            case "--register":
                Registration.RegisterAndOpenSettings();
                Shutdown();
                return;
            case Registration.MachineArg: // elevated helper launched by --register
                try
                {
                    var exe = e.Args.Length > 1 ? e.Args[1] : Environment.ProcessPath!;
                    Registration.Log($"machine: elevated={Registration.IsElevated} exe={exe}");
                    Registration.Register(Microsoft.Win32.Registry.LocalMachine, exe);
                    Registration.Log("machine: ok");
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    Registration.Log("machine failed: " + ex);
                    Shutdown(1);
                }
                return;
            case "--unregister":
                Registration.Unregister();
                Shutdown();
                return;
            case "--default-apps": // run by the installer when it finishes
                Registration.OpenDefaultAppsSettings(machine: true);
                Shutdown();
                return;
            case "--cleanup-user":
                Registration.RemoveTree(Microsoft.Win32.Registry.CurrentUser);
                Registration.NotifyAssocChanged();
                Shutdown();
                return;
            case "--unregister-machine":
                Registration.RemoveTree(Microsoft.Win32.Registry.LocalMachine);
                Shutdown();
                return;
        }

        var window = new MainWindow(arg);
        MainWindow = window;
        window.Show();
    }
}
