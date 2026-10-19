using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

/// <summary>
/// Sign-in through the browser (<see cref="BrowserSignInFlow"/>), which works for every account — one created with Google
/// has no password to type here. Email and password, then an authenticator or recovery code for an account with 2FA,
/// stay behind "Use a password instead" (decided 2026-10-19).
/// </summary>
public sealed class LoginForm : Form
{
    private const int CollapsedHeight = 210;
    private const int ExpandedHeight = 400;

    private readonly ApiClient _apiClient;
    private readonly BrowserSignInFlow _browserSignIn;
    private readonly Button _browserButton;
    private readonly LinkLabel _passwordLink;
    private readonly Panel _passwordPanel;
    private readonly TextBox _emailBox;
    private readonly TextBox _passwordBox;
    private readonly Label _codeLabel;
    private readonly TextBox _codeBox;
    private readonly Button _loginButton;
    private readonly Label _statusLabel;

    private string? _pendingAuthToken;
    private CancellationTokenSource? _browserWait;

    public LoginForm(ApiClient apiClient, AppConfig config)
    {
        _apiClient = apiClient;
        _browserSignIn = new BrowserSignInFlow(apiClient, config);

        Text = "Activity Tracker - Login";
        ClientSize = new Size(330, CollapsedHeight);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var intro = new Label
        {
            Text = "Sign in with your account in the browser — Google sign-in and two-factor authentication work there.",
            Location = new Point(20, 15),
            AutoSize = true,
            MaximumSize = new Size(290, 0)
        };

        _browserButton = new Button { Text = "Sign in with the browser", Location = new Point(20, 62), Width = 290, Height = 34 };
        _browserButton.Click += OnBrowserClicked;

        _passwordLink = new LinkLabel { Text = "Use a password instead", Location = new Point(20, 108), AutoSize = true };
        _passwordLink.LinkClicked += (_, _) => ShowPasswordSignIn();

        _passwordPanel = new Panel { Location = new Point(0, 140), Size = new Size(330, 190), Visible = false };

        var emailLabel = new Label { Text = "Email:", Location = new Point(20, 0), AutoSize = true };
        _emailBox = new TextBox { Location = new Point(20, 20), Width = 290 };

        var passwordLabel = new Label { Text = "Password:", Location = new Point(20, 50), AutoSize = true };
        _passwordBox = new TextBox { Location = new Point(20, 70), Width = 290, PasswordChar = '•' };

        _codeLabel = new Label
        {
            Text = "Authenticator or recovery code:",
            Location = new Point(20, 100),
            AutoSize = true,
            Visible = false
        };
        _codeBox = new TextBox { Location = new Point(20, 120), Width = 290, Visible = false };

        _loginButton = new Button { Text = "Login", Location = new Point(20, 155), Width = 290, Height = 30 };
        _loginButton.Click += OnLoginClicked;

        _passwordPanel.Controls.AddRange([emailLabel, _emailBox, passwordLabel, _passwordBox, _codeLabel, _codeBox, _loginButton]);

        _statusLabel = new Label
        {
            Text = "",
            Location = new Point(20, 140),
            AutoSize = true,
            MaximumSize = new Size(290, 0),
            ForeColor = Color.Red
        };

        Controls.AddRange([intro, _browserButton, _passwordLink, _passwordPanel, _statusLabel]);

        AcceptButton = _browserButton;
        FormClosing += (_, _) => _browserWait?.Cancel();
    }

    private void ShowPasswordSignIn()
    {
        _passwordLink.Visible = false;
        _passwordPanel.Visible = true;
        _statusLabel.Location = new Point(20, 340);
        ClientSize = new Size(330, ExpandedHeight);
        AcceptButton = _loginButton;
        _emailBox.Focus();
    }

    private async void OnBrowserClicked(object? sender, EventArgs e)
    {
        // The same button cancels a sign-in the browser has not answered yet.
        if (_browserWait is { } running)
        {
            running.Cancel();
            return;
        }

        using var wait = new CancellationTokenSource();
        _browserWait = wait;
        _browserButton.Text = "Cancel";
        _loginButton.Enabled = false;
        ShowStatus("Finish signing in in the browser that just opened…", Color.Gray);

        LoginOutcome? outcome = null;
        try
        {
            outcome = await _browserSignIn.RunAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled here, or the form is closing.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Browser sign-in failed");
            outcome = new LoginOutcome(LoginStatus.Failed, Message: "Couldn't open the browser.");
        }
        finally
        {
            _browserWait = null;
        }

        // Closed while the browser was still out: nothing left to report to.
        if (IsDisposed || !Visible)
            return;

        _browserButton.Text = "Sign in with the browser";
        _loginButton.Enabled = true;

        switch (outcome?.Status)
        {
            case LoginStatus.SignedIn:
                DialogResult = DialogResult.OK;
                Close();
                return;
            case null:
                ShowStatus("", Color.Gray);
                break;
            default:
                ShowStatus(outcome.Message ?? "Login failed.", Color.Red);
                break;
        }

        // The browser had the focus; bring the form back to say how it went.
        Activate();
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        _loginButton.Enabled = false;
        _browserButton.Enabled = false;
        ShowStatus("Logging in...", Color.Gray);

        var outcome = _pendingAuthToken is null
            ? await _apiClient.LoginAsync(_emailBox.Text.Trim(), _passwordBox.Text)
            : await _apiClient.CompleteTwoFactorAsync(_pendingAuthToken, _codeBox.Text.Trim());

        switch (outcome.Status)
        {
            case LoginStatus.SignedIn:
                DialogResult = DialogResult.OK;
                Close();
                return;

            case LoginStatus.TwoFactorRequired:
                _pendingAuthToken = outcome.PendingAuthToken;
                _emailBox.Enabled = false;
                _passwordBox.Enabled = false;
                _codeLabel.Visible = true;
                _codeBox.Visible = true;
                _codeBox.Focus();
                _loginButton.Text = "Verify";
                ShowStatus("Enter the code from your authenticator app.", Color.Gray);
                break;

            default:
                ShowStatus(outcome.Message ?? "Login failed.", Color.Red);
                break;
        }

        _loginButton.Enabled = true;
        _browserButton.Enabled = true;
    }

    private void ShowStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }
}
