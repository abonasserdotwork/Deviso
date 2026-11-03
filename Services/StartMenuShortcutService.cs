using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Deviso.Services;

public sealed class StartMenuShortcutService
{
    private const string ShortcutName = "Deviso.lnk";
    private readonly string? _shortcutPath;

    public StartMenuShortcutService()
    {
        var programsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrWhiteSpace(programsFolder))
        {
            _shortcutPath = Path.Combine(programsFolder, "Deviso", ShortcutName);
        }
    }

    public bool IsAvailable => _shortcutPath is not null;
    public bool IsInstalled => _shortcutPath is not null && File.Exists(_shortcutPath);

    public void RefreshIfInstalled()
    {
        if (IsInstalled)
        {
            Create();
        }
    }

    public void Create()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("Could not locate the running Deviso executable.");
        }

        var shortcutPath = _shortcutPath
            ?? throw new InvalidOperationException("The current user's Start menu Programs folder is unavailable.");
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        object? shell = null;
        object? shortcut = null;
        object? shellLink = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Windows Script Host is unavailable; the Start menu shortcut could not be created.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not initialize Windows Script Host.");
            dynamic shellAutomation = shell;
            shortcut = shellAutomation.CreateShortcut(shortcutPath);
            dynamic link = shortcut;
            link.TargetPath = executable;
            link.WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
            link.Description = "Deviso drive health";
            link.IconLocation = $"{executable},0";
            link.Save();

            var shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
                ?? throw new InvalidOperationException("Windows could not update the Start menu shortcut.");
            shellLink = Activator.CreateInstance(shellLinkType)
                ?? throw new InvalidOperationException("Windows could not initialize the Start menu shortcut.");
            var persistence = (IPersistFile)shellLink;
            persistence.Load(shortcutPath, 2);
            WindowsTaskbarIdentity.SetShortcutAppUserModelId(shellLink);
            persistence.Save(shortcutPath, false);
        }
        finally
        {
            foreach (var comObject in new[] { shellLink, shortcut, shell })
            {
                if (comObject is not null && Marshal.IsComObject(comObject))
                {
                    Marshal.FinalReleaseComObject(comObject);
                }
            }
        }
    }

    public void Remove()
    {
        if (_shortcutPath is not null && File.Exists(_shortcutPath))
        {
            File.Delete(_shortcutPath);
        }
    }

}
