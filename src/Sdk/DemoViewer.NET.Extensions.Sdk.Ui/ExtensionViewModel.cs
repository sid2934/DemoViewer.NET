#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Ui;

/// <summary>
///     A base for an extension's view models: observable, and resolved to its view by the host's naming rule
///     (<see cref="IExtensionViewModel" />), so a pane, panel, flyout or nested <c>ContentControl</c> shows the view
///     without a hand-written factory.
/// </summary>
public abstract class ExtensionViewModel : ObservableObject, IExtensionViewModel;
