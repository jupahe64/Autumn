using Autumn.GUI;
using Autumn.IO;
using Autumn.Storage;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

#if DEBUG
RomFSHandler.RomFSPath = ""; // Please, input your RomFS manually here.

StageHandler.TryImportStage("FirstStage", 1, out Stage stage);
ProjectHandler.ActiveProject = new();
ProjectHandler.ActiveProject.Stages.Add(stage);
#endif

SettingsHandler.LoadSettings();

RomFSHandler.LoadFromSettings();

if (!SettingsHandler.GetValue<bool>("SkipWelcomeWindow"))
    WindowManager.Add(new WelcomeWindowContext());
else
    WindowManager.Add(new MainWindowContext());

WindowManager.Run();

SettingsHandler.SaveSettings();
