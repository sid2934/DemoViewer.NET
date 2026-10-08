namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     Marks a view model the host builds a view for by naming: its full type name with <c>ViewModel</c> replaced
///     by <c>View</c>, found in the view model's own assembly. Without it a view model bound to a bare
///     <c>ContentControl</c> renders as its type name.
/// </summary>
/// <remarks>
///     <c>MyExtension.ViewModels.StatsPaneViewModel</c> resolves to <c>MyExtension.Views.StatsPaneView</c>, which
///     needs a public parameterless constructor. <c>ExtensionViewModel</c> in the UI kit implements this.
/// </remarks>
public interface IExtensionViewModel;
