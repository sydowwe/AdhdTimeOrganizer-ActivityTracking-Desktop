using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

/// <summary>
/// Email and password, then — for an account with 2FA — an authenticator or recovery code.
/// </summary>
public sealed class LoginForm : Form
{
    private readonly ApiClient _apiClient;
    private readonly TextBox _emailBox;
    private readonly TextBox _passwordBox;
    private readonly Label _codeLabel;
    private readonly TextBox _codeBox;
    private readonly Button _loginButton;
    private readonly Label _statusLabel;

    private string? _pendingAuthToken;

    public LoginForm(ApiClient apiClient)
    {
        _apiClient = apiClient;

        Text = "Activity Tracker - Login";
        Size = new Size(350, 290);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var emailLabel = new Label { Text = "Email:", Location = new Point(20, 20), AutoSize = true };
        _emailBox = new TextBox { Location = new Point(20, 40), Width = 290 };

        var passwordLabel = new Label { Text = "Password:", Location = new Point(20, 70), AutoSize = true };
        _passwordBox = new TextBox { Location = new Point(20, 90), Width = 290, PasswordChar = '•' };

        _codeLabel = new Label
        {
            Text = "Authenticator or recovery code:",
            Location = new Point(20, 120),
            AutoSize = true,
            Visible = false
        };
        _codeBox = new TextBox { Location = new Point(20, 140), Width = 290, Visible = false };

        _loginButton = new Button { Text = "Login", Location = new Point(20, 175), Width = 290, Height = 30 };
        _loginButton.Click += OnLoginClicked;

        _statusLabel = new Label
        {
            Text = "",
            Location = new Point(20, 212),
            AutoSize = true,
            MaximumSize = new Size(290, 0),
            ForeColor = Color.Red
        };

        Controls.AddRange([emailLabel, _emailBox, passwordLabel, _passwordBox, _codeLabel, _codeBox, _loginButton, _statusLabel]);

        AcceptButton = _loginButton;
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        _loginButton.Enabled = false;
        _statusLabel.Text = "Logging in...";
        _statusLabel.ForeColor = Color.Gray;

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
                _statusLabel.Text = "Enter the code from your authenticator app.";
                _statusLabel.ForeColor = Color.Gray;
                break;

            default:
                _statusLabel.Text = outcome.Message ?? "Login failed.";
                _statusLabel.ForeColor = Color.Red;
                break;
        }

        _loginButton.Enabled = true;
    }
}
