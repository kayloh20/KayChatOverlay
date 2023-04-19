using System;
using System.Collections.Generic;
using System.Windows;
using TwitchLib.Client.Models;
using TwitchLib.Client;
using System.Diagnostics;
using TwitchLib.Client.Events;
using TwitchLib.Communication.Models;
using TwitchLib.Communication.Clients;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using static System.Formats.Asn1.AsnWriter;
using System.Threading.Tasks;

namespace KayChatOverlayWPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private static List<string> scopes = new List<string> { "chat:read" };

        private TwitchClient? client;
        private readonly List<ChatMessage> messageQueue = new();
        private int queuePosition = -1;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;

        private const uint VK_PRIOR = 0x21; // Page Up
        private const uint VK_NEXT = 0x22; // Page Down

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        // This static method is required because legacy OSes do not support
        // SetWindowLongPtr
        public static IntPtr SetWindowLongPtr(HandleRef hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            else
                return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(HandleRef hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(HandleRef hWnd, int nIndex, IntPtr dwNewLong);

        const int WS_EX_TRANSPARENT = 0x00000020;
        const int GWL_EXSTYLE = (-20);


        public MainWindow()
        {
            InitializeComponent();

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            double windowWidth = this.Width;
            double windowHeight = this.Height;
            this.Left = (screenWidth / 2) - (windowWidth / 2);
            this.Top = (screenHeight / 10) - (windowHeight / 2);

            messageLabel.Content = string.Empty;
            queueLabel.Content = "No new messages";

            StartChatListenerAsync().WaitAsync(new TimeSpan(0, 1, 0));
        }

        private IntPtr _windowHandle;
        private HwndSource? _source;
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _windowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(HwndHook);

            RegisterHotKey(_windowHandle, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN, VK_PRIOR);
            RegisterHotKey(_windowHandle, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN, VK_NEXT);

            var extendedStyle = GetWindowLongPtr(_windowHandle, GWL_EXSTYLE);
            _ = SetWindowLongPtr(new HandleRef(this, _windowHandle), GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            int vkey = (int)((lParam >> 16) & 0xFFFF);

            switch (msg)
            {
                case WM_HOTKEY:
                    switch (wParam.ToInt32())
                    {
                        case HOTKEY_ID:
                            if (vkey == VK_PRIOR)
                            {
                                if (queuePosition - 1 >= 0)
                                {
                                    queuePosition -= 1;

                                    RefreshMessage();

                                    RefreshQueueStatus();
                                }
                            }
                            else if (vkey == VK_NEXT)
                            {
                                if (queuePosition + 1 < messageQueue.Count)
                                {
                                    queuePosition += 1;

                                    RefreshMessage();

                                    RefreshQueueStatus();
                                }
                            }
                            handled = true;
                            break;
                    }
                    break;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            _source?.RemoveHook(HwndHook);
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            base.OnClosed(e);
        }

        private async Task StartChatListenerAsync()
        {
            // create twitch api instance
            var api = new TwitchLib.Api.TwitchAPI();
            api.Settings.ClientId = Config.TwitchClientId;

            var server = new WebServer(Config.TwitchRedirectUri);

            // print out auth url
            Debug.WriteLine($"Please authorize here:\n{getAuthorizationCodeUrl(Config.TwitchClientId, Config.TwitchRedirectUri, scopes)}");

            // listen for incoming requests
            var auth = await server.Listen();

            // exchange auth code for oauth access/refresh
            var resp = await api.Auth.GetAccessTokenFromCodeAsync(auth.Code, Config.TwitchClientSecret, Config.TwitchRedirectUri);

            // update TwitchLib's api with the recently acquired access token
            api.Settings.AccessToken = resp.AccessToken;

            var credentials = new ConnectionCredentials("kayloh20", resp.AccessToken);
            var clientOptions = new ClientOptions
            {
                MessagesAllowedInPeriod = 750,
                ThrottlingPeriod = TimeSpan.FromSeconds(30),
            };
            var customClient = new WebSocketClient(clientOptions);
            client = new TwitchClient(customClient);
            client.Initialize(credentials, "kayloh20");

            client.OnLog += Client_OnLog;
            client.OnMessageReceived += Client_OnMessageReceived;
            client.OnNewSubscriber += Client_OnNewSubscriber;
            client.OnConnected += Client_OnConnected;

            client.Connect();
        }

        private void Client_OnLog(object? sender, OnLogArgs e)
        {
            Debug.WriteLine($"{e.DateTime}: {e.BotUsername} - {e.Data}");
        }

        private void Client_OnConnected(object? sender, OnConnectedArgs e)
        {
            Debug.WriteLine($"Connected to {e.AutoJoinChannel}");
        }

        private void Client_OnMessageReceived(object? sender, OnMessageReceivedArgs e)
        {
            messageQueue.Add(e.ChatMessage);

            if (queuePosition == -1)
            {
                queuePosition += 1;

                RefreshMessage();
            }
            else
            {
                RefreshQueueStatus();
            }
        }

        private void Client_OnNewSubscriber(object? sender, OnNewSubscriberArgs e)
        {

        }

        private void RefreshMessage()
        {
            var chatMessage = messageQueue[queuePosition];

            Dispatcher.Invoke(() =>
            {
                var timestamp = long.Parse(chatMessage.TmiSentTs);

                var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime();

                messageLabel.Content = $"{date:hh:mm:ss tt} {chatMessage.DisplayName}: {chatMessage.Message}";
            });
        }

        private void RefreshQueueStatus()
        {
            var diff = messageQueue.Count - queuePosition - 1;

            if (diff == 0)
            {
                Dispatcher.Invoke(() =>
                {
                    queueLabel.Content = "No new messages";
                });
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    queueLabel.Content = $"{diff} new messages";
                });
            }
        }

        private static string getAuthorizationCodeUrl(string clientId, string redirectUri, List<string> scopes)
        {
            var scopesStr = String.Join('+', scopes);

            return "https://id.twitch.tv/oauth2/authorize?" +
                   $"client_id={clientId}&" +
                   $"redirect_uri={System.Web.HttpUtility.UrlEncode(redirectUri)}&" +
                   "response_type=code&" +
                   $"scope={scopesStr}";
        }
    }
}
