using EgressController.App.Preview;
using EgressController.App.ViewModels;
using EgressController.Core.Models;

namespace EgressController.Windows.IntegrationTests;

public sealed class ApplicationListRefreshTests
{
    [Fact]
    public async Task Category_switch_is_one_publication_not_hundreds_of_row_moves()
    {
        var backend = new PreviewController("empty");
        for (int i = 0; i < 500; i++)
            await backend.AddManualApplicationAsync($@"C:\Mock\App{i:000}\app.exe");
        var apps = new AppsViewModel(backend);
        foreach (var entry in apps.Entries.Take(250).ToArray())
            await backend.SetApplicationCategoryAsync(entry.Target, "office");
        apps.RefreshStatuses();
        var previous = apps.Entries;
        int oldCollectionEvents = 0, publications = 0;
        previous.CollectionChanged += (_, _) => oldCollectionEvents++;
        apps.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(apps.Entries)) publications++; };
        apps.SelectedCategory = apps.Categories.Single(item => item.Id == "");
        Assert.Equal(250, apps.Entries.Count);
        Assert.Equal(0, oldCollectionEvents);
        Assert.Equal(1, publications);
        var visible = apps.Entries.ToArray();
        apps.RefreshStatuses();
        Assert.Equal(1, publications);
        Assert.Equal(visible, apps.Entries);
    }

    [Fact]
    public async Task Refresh_preserves_instances_order_and_appends_new_entries()
    {
        var backend = new PreviewController();
        var apps = new AppsViewModel(backend);
        var collection = apps.Entries;
        var original = collection.ToArray();
        backend.ScanOverride = targets => targets.Reverse().Prepend(new LaunchTarget
        { Id = "new", Name = "First alphabetically", CanonicalExecutable = @"C:\Mock\New\app.exe" }).ToArray();
        int movesOrResets = 0;
        collection.CollectionChanged += (_, args) =>
        {
            if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Move
                or System.Collections.Specialized.NotifyCollectionChangedAction.Reset) movesOrResets++;
        };
        await apps.ScanCommand.ExecuteAsync(null);
        Assert.Same(collection, apps.Entries);
        Assert.Equal(original, apps.Entries.Take(original.Length));
        Assert.Equal(original.Length + 1, apps.Entries.Count);
        Assert.Equal(0, movesOrResets);
    }
}
