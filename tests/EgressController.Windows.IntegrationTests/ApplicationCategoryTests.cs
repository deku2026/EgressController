using EgressController.App.Preview;
using EgressController.App.ViewModels;
using EgressController.Core.Models;
using EgressController.Presentation;

namespace EgressController.Windows.IntegrationTests;

public sealed class ApplicationCategoryTests
{
    [Fact]
    public async Task Manual_category_survives_refresh_without_changing_routes_or_protection()
    {
        var backend = new PreviewController(); var vm = new AppsViewModel(backend);
        var app = vm.Entries.Single(item => item.Name == "英雄联盟");
        var profile = backend.Profile; var readiness = backend.Protection;
        Assert.Equal("", app.CategoryId); Assert.False(app.Routing.IsSelected);
        app.SelectedCategory = ApplicationCategory.BuiltIn.Single(category => category.Id == "games");
        Assert.Equal("games", app.CategoryId); Assert.Same(profile, backend.Profile); Assert.Equal(readiness, backend.Protection);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.SelectedCategory = vm.Categories.Single(category => category.Id == "games");
        Assert.Equal("英雄联盟", Assert.Single(vm.Entries).Name);
        Assert.False(vm.Entries[0].Routing.IsSelected); Assert.Same(profile, backend.Profile);
        Assert.Equal(0, backend.InventoryBuildCount);
        vm.Entries[0].Routing.IsSelected = true;
        Assert.Equal(1, backend.InventoryBuildCount);
        Assert.True(vm.Entries[0].Target.InventoryReady);
    }
    [Fact]
    public async Task Bulk_actions_apply_only_to_the_visible_category_and_search()
    {
        var backend = new PreviewController(); var vm = new AppsViewModel(backend);
        vm.Query = "英雄联盟";
        await vm.SelectAllCommand.ExecuteAsync(null);
        Assert.Equal(4, backend.Profile.Applications.Count);
        await vm.ClearAllCommand.ExecuteAsync(null);
        Assert.Equal(3, backend.Profile.Applications.Count);
    }
    [Fact]
    public void White_and_pale_artwork_uses_dark_backing_while_dark_and_colored_artwork_stays_light()
    {
        Assert.True(IconContrast.IsLightArtwork([255,255,255,255], 4, 1, 1));
        Assert.True(IconContrast.IsLightArtwork([220,224,230,255], 4, 1, 1));
        Assert.False(IconContrast.IsLightArtwork([20,20,20,255], 4, 1, 1));
        Assert.False(IconContrast.IsLightArtwork([255,255,255,0], 4, 1, 1));
        Assert.False(IconContrast.IsLightArtwork([40,80,190,255], 4, 1, 1));
    }
}
