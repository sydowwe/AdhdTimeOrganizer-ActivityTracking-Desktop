using DesktopActivityTracker.Services;

namespace DesktopActivityTracker;

public sealed class LoginForm : Form
{
    private readonly ApiClient _apiClient;
    private readonly TextBox _emailBox;
    private readonly TextBox _passwordBox;
    private readonly Button _loginButton;
    private readonly Label _statusLabel;

    public LoginForm(ApiClient apiClient)
    {
        _apiClient = apiClient;

        Text = "Activity Tracker - Login";
        Size = new Size(350, 220);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var emailLabel = new Label { Text = "Email:", Location = new Point(20, 20), AutoSize = true };
        _emailBox = new TextBox { Location = new Point(20, 40), Width = 290 };

        var passwordLabel = new Label { Text = "Password:", Location = new Point(20, 70), AutoSize = true };
        _passwordBox = new TextBox { Location = new Point(20, 90), Width = 290, PasswordChar = '•' };

        _loginButton = new Button { Text = "Login", Location = new Point(20, 130), Width = 290, Height = 30 };
        _loginButton.Click += OnLoginClicked;

        _statusLabel = new Label
        {
            Text = "",
            Location = new Point(20, 165),
            AutoSize = true,
            ForeColor = Color.Red
        };

        Controls.AddRange([emailLabel, _emailBox, passwordLabel, _passwordBox, _loginButton, _statusLabel]);

        AcceptButton = _loginButton;
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        _loginButton.Enabled = false;
        _statusLabel.Text = "Logging in...";
        _statusLabel.ForeColor = Color.Gray;

        var success = await _apiClient.LoginAsync(_emailBox.Text.Trim(), _passwordBox.Text);

        if (success)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            _statusLabel.Text = "Login failed. Check credentials.";
            _statusLabel.ForeColor = Color.Red;
            _loginButton.Enabled = true;
        }
    }
}
