using CommunityToolkit.Mvvm.ComponentModel;
using Talky.Services.Networking;

namespace Talky.ViewModels;

/// <summary>Backs the add / edit friend dialog.</summary>
public sealed partial class FriendEditorViewModel : ObservableObject
{
    public FriendEditorViewModel(string title, string primaryButtonText, string? nickname, string? address, IReadOnlyList<string> ownAddresses)
    {
        Title = title;
        PrimaryButtonText = primaryButtonText;
        Nickname = nickname ?? string.Empty;
        Address = address ?? string.Empty;
        OwnAddresses = ownAddresses;
    }

    public string Title { get; }

    public string PrimaryButtonText { get; }

    public IReadOnlyList<string> OwnAddresses { get; }

    public string OwnAddressText => OwnAddresses.Count switch
    {
        0 => "Couldn't detect your LAN address.",
        1 => $"Your address is {OwnAddresses[0]} — share it so friends can add you too.",
        _ => $"Your addresses: {string.Join(", ", OwnAddresses)} — share one so friends can add you too.",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial string Nickname { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(AddressError), nameof(HasAddressError))]
    public partial string Address { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Nickname) && Nickname.Trim().Length <= 64 && TryGetEndpoint(out _, out _);

    public string? AddressError =>
        string.IsNullOrWhiteSpace(Address) || TryGetEndpoint(out _, out _)
            ? null
            : "Use an IP address or PC name, optionally with a port — e.g. 192.168.1.20 or 192.168.1.20:47821.";

    public bool HasAddressError => AddressError is not null;

    public bool TryGetEndpoint(out string host, out int port) => NetworkInfo.TryParseAddress(Address, out host, out port);
}
