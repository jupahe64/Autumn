using Autumn.IO;
using TinyFileDialogsSharp;

namespace Autumn.Commands;

internal static class CommandGenerator
{
    public static Command NewProject() =>
        new(
            displayName: "New Project",
            displayShortcut: string.Empty,
            action: () =>
            {
                bool success = TinyFileDialogs.SelectFolderDialog(
                    out string? output,
                    title: "Select where to save the new Autumn project.",
                    defaultPath: RecentHandler.LastProjectSavePath
                );

                if (success)
                {
                    RecentHandler.LastProjectSavePath = output!;
                    ProjectHandler.CreateNew(output!);
                }
            },
            enabled: true
        );

    public static Command OpenProject() =>
        new(
            displayName: "Open Project",
            displayShortcut: string.Empty,
            action: () =>
            {
                bool success = TinyFileDialogs.OpenFileDialog(
                    out string[]? output,
                    title: "Select the Autumn project file.",
                    defaultPath: RecentHandler.LastProjectOpenPath,
                    filterPatterns: new string[] { "*.yml", ".yaml" },
                    filterDescription: "YAML file"
                );

                if (success)
                {
                    string projectPath = output![0];
                    RecentHandler.LastProjectOpenPath =
                        Path.GetDirectoryName(projectPath)
                        ?? Directory.GetDirectoryRoot(projectPath);

                    ProjectHandler.LoadProject(projectPath);
                }
            },
            enabled: true
        );
}
