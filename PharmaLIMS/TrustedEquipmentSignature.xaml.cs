using PharmaLIMS.Infrastructure;
using PharmaLIMS.TrustedSigning;
using System;
using System.Threading;
using System.Windows;

namespace PharmaLIMS;

public partial class TrustedEquipmentSignature : Window
{
    private readonly EquipmentSigningRequest _request;
    private readonly string _username;
    private readonly WindowsTrustedPipeClient _client;
    private readonly CancellationTokenSource _stop = new();
    private bool _closed;
    public bool IsConfirmed { get; private set; }

    public TrustedEquipmentSignature(EquipmentSigningRequest request, string username, WindowsTrustedPipeClient client)
    {
        InitializeComponent();
        _request = request; _username = username; _client = client;
        LblRecord.Text = request.EquipmentCode + " — " + request.Action;
        LblSigner.Text = "PharmaLIMS account: " + username;
        Closed += (_, _) => { _closed = true; _stop.Cancel(); TxtPassword.Clear(); };
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        string reason = TxtReason.Text.Trim();
        if (reason.Length < 10) { LblError.Text = "Reason must contain at least 10 characters."; return; }
        if (string.IsNullOrEmpty(TxtPassword.Password)) { LblError.Text = "Enter your PharmaLIMS password."; return; }
        string password = TxtPassword.Password;
        TxtPassword.Clear();
        BtnConfirm.IsEnabled = false;
        LblError.Text = "";
        try
        {
            var request = _request with { RequestId = Guid.NewGuid(), IssuedAtUtc = DateTimeOffset.UtcNow, Reason = reason };
            await _client.CommitAsync(request, _username, password, _stop.Token);
            if (_closed) return;
            IsConfirmed = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            if (!_closed) LblError.Text = UserFacingError.SafeMessage(ex, "Trusted equipment signing failed. No local signing fallback was used.");
        }
        finally { password = ""; TxtPassword.Clear(); if (!_closed) BtnConfirm.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
