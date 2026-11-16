using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class DefaultConfigurationTests
{
    [TestMethod]
    public void DefaultTreeIsValidAndUsesPortableTargets()
    {
        var config = DefaultConfiguration.Create(key => key);
        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.IsTrue(store.Validate(config).IsValid, string.Join("; ", store.Validate(config).Errors));
        var actions = config.Items.SelectMany(group => group.Children).SelectMany(node => node.Steps).ToArray();
        Assert.AreEqual(7, actions.Count(action => action.Kind == ActionKind.OpenFolder && action.Path.StartsWith("{knownFolder:")));
        Assert.AreEqual(4, actions.Count(action => action.Kind == ActionKind.RunProgram && action.Path.StartsWith("{env:SystemRoot}")));
        Assert.AreEqual(10, actions.Count(action => action.Kind == ActionKind.SendShortcut && HotkeyParser.TryParse(action.Shortcut, out _, out _)));
        Assert.IsTrue(config.Items.Concat(config.Items.SelectMany(group => group.Children))
            .All(node => node.IconPath.StartsWith(Path.Combine("icon-packs", "Boxicons") + Path.DirectorySeparatorChar) && node.IconColor.Length == 7));
        Assert.IsTrue(config.Items.SelectMany(group => group.Children).All(node => string.IsNullOrEmpty(node.Hotkey)));
    }
}
