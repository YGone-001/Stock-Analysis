using System.Windows;
using System.Windows.Media;

namespace AIHelper.Views;

/// <summary>WPF-only visual/logical tree helper retained for recovered Sparrow UI.</summary>
public static class SparrowUiTreeHelper
{
    public static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T target) return target;
            DependencyObject? next = LogicalTreeHelper.GetParent(current);
            if (next == null && current is Visual) next = VisualTreeHelper.GetParent(current);
            current = next;
        }
        return null;
    }
}
