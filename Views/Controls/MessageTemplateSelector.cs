using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Talky.Models;
using Talky.ViewModels;

namespace Talky.Views.Controls;

public sealed partial class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }

    public DataTemplate? CallEventTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is MessageViewModel { Kind: MessageKind.CallEvent } ? CallEventTemplate : TextTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
