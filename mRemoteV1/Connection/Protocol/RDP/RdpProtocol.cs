using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AxMSTSCLib;
using mRemoteNG.App;
using mRemoteNG.Messages;
using mRemoteNG.Security.SymmetricEncryption;
using mRemoteNG.Tools;
using mRemoteNG.UI.Forms;
using MSTSCLib;

namespace mRemoteNG.Connection.Protocol.RDP
{
	public partial class RdpProtocol : ProtocolBase
	{
        /* RDP v8 requires Windows 7 with:
         * https://support.microsoft.com/en-us/kb/2592687 
         * OR
         * https://support.microsoft.com/en-us/kb/2923545
         * 
         * Windows 8+ support RDP v8 out of the box.
         */
        private MsRdpClient8NotSafeForScripting _rdpClient;
        private Version _rdpVersion;
        private ConnectionInfo _connectionInfo;
        private bool _loginComplete;
        private bool _redirectKeys;
        private bool _alertOnIdleDisconnect;
        private readonly FrmMain _frmMain = FrmMain.Default;

        #region Properties
        public bool SmartSize
		{
			get
			{
				return _rdpClient.AdvancedSettings2.SmartSizing;
			}
            private set
			{
				_rdpClient.AdvancedSettings2.SmartSizing = value;
				ReconnectForResize();
			}
		}
		
        public bool Fullscreen
		{
			get
			{
				return _rdpClient.FullScreen;
			}
            private set
			{
				_rdpClient.FullScreen = value;
				ReconnectForResize();
			}
		}

	    private bool RedirectKeys
		{
/*
			get
			{
				return _redirectKeys;
			}
*/
			set
			{
				_redirectKeys = value;
				try
				{
					if (!_redirectKeys)
					{
						return;
					}
							
					Debug.Assert(Convert.ToBoolean(_rdpClient.SecuredSettingsEnabled));
                    var msRdpClientSecuredSettings = _rdpClient.SecuredSettings2;
					msRdpClientSecuredSettings.KeyboardHookMode = 1; // Apply key combinations at the remote server.
				}
				catch (Exception ex)
				{
					Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetRedirectKeysFailed, ex);
				}
			}
		}

        public bool LoadBalanceInfoUseUtf8 { get; set; }
        #endregion

        #region Constructors
        public RdpProtocol()
        {
            Control = new AxMsRdpClient8NotSafeForScripting();
        }
        #endregion

        #region Public Methods
		public override bool Initialize()
		{
			base.Initialize();
			try
			{
				Control.CreateControl();
				_connectionInfo = InterfaceControl.Info;
				
				try
				{
					while (!Control.Created)
					{
						Thread.Sleep(0);
                        Application.DoEvents();
					}
                    _rdpClient = (MsRdpClient8NotSafeForScripting)((AxMsRdpClient8NotSafeForScripting)Control).GetOcx();

				}
				catch (System.Runtime.InteropServices.COMException ex)
				{
					Runtime.MessageCollector.AddExceptionMessage(Language.strRdpControlCreationFailed, ex);
					Control.Dispose();
					return false;
				}
						
				_rdpVersion = new Version(_rdpClient.Version);
						
				_rdpClient.Server = _connectionInfo.Hostname;

                SetCredentials();
                SetResolution();
                _rdpClient.FullScreenTitle = _connectionInfo.Name;

                _alertOnIdleDisconnect = _connectionInfo.RDPAlertIdleTimeout;
                _rdpClient.AdvancedSettings2.MinutesToIdleTimeout = _connectionInfo.RDPMinutesToIdleTimeout;

                //not user changeable
                _rdpClient.AdvancedSettings2.GrabFocusOnConnect = true;
				_rdpClient.AdvancedSettings3.EnableAutoReconnect = true;
				_rdpClient.AdvancedSettings3.MaxReconnectAttempts = Settings.Default.RdpReconnectionCount;
				_rdpClient.AdvancedSettings2.keepAliveInterval = 60000; //in milliseconds (10,000 = 10 seconds)
				_rdpClient.AdvancedSettings5.AuthenticationLevel = 0;
				_rdpClient.AdvancedSettings2.EncryptionEnabled = 1;
						
				_rdpClient.AdvancedSettings2.overallConnectionTimeout = Settings.Default.ConRDPOverallConnectionTimeout;
						
				_rdpClient.AdvancedSettings2.BitmapPeristence = Convert.ToInt32(_connectionInfo.CacheBitmaps);
				if (_rdpVersion >= RdpVersion.RDC61)
				{
					_rdpClient.AdvancedSettings7.EnableCredSspSupport = _connectionInfo.UseCredSsp;
				    _rdpClient.AdvancedSettings8.AudioQualityMode = (uint)_connectionInfo.SoundQuality;
				}

                SetUseConsoleSession();
                SetPort();
				RedirectKeys = _connectionInfo.RedirectKeys;
                SetRedirection();
                SetAuthenticationLevel();
				SetLoadBalanceInfo();
                SetRdGateway();
						
				_rdpClient.ColorDepth = (int)_connectionInfo.Colors;

                SetPerformanceFlags();
						
				_rdpClient.ConnectingText = Language.strConnecting;
						
				Control.Anchor = AnchorStyles.None;
						
				return true;
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetPropsFailed, ex);
				return false;
			}
		}
				
		public override bool Connect()
		{
			_loginComplete = false;
			SetEventHandlers();

            try
			{
				_rdpClient.Connect();
				base.Connect();
				return true;
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strConnectionOpenFailed, ex);
			}
					
			return false;
		}
				
		public override void Disconnect()
		{
			try
			{
				_rdpClient.Disconnect();
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpDisconnectFailed, ex);
				Close();
			}
		}
				
		public void ToggleFullscreen()
		{
			try
			{
                Fullscreen = !Fullscreen;
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpToggleFullscreenFailed, ex);
			}
		}
				
		public void ToggleSmartSize()
		{
			try
			{
                SmartSize = !SmartSize;
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpToggleSmartSizeFailed, ex);
			}
		}
				
		public override void Focus()
		{
			try
			{
				if (Control.ContainsFocus == false)
				{
					Control.Focus();
				}
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpFocusFailed, ex);
			}
		}
				
		private Size _controlBeginningSize;
		public override void ResizeBegin(object sender, EventArgs e)
		{
			_controlBeginningSize = Control.Size;
		}
				
		public override void Resize(object sender, EventArgs e)
		{
			if (DoResize() && _controlBeginningSize.IsEmpty)
			{
				ReconnectForResize();
			}
			base.Resize(sender, e);
		}
				
		public override void ResizeEnd(object sender, EventArgs e)
		{
			DoResize();
			if (!(Control.Size == _controlBeginningSize))
			{
				ReconnectForResize();
			}
			_controlBeginningSize = Size.Empty;
		}
        #endregion
		
        #region Private Methods
		private bool DoResize()
		{
			Control.Location = InterfaceControl.Location;
			if (!(Control.Size == InterfaceControl.Size) && !(InterfaceControl.Size == Size.Empty)) // kmscode - this doesn't look right to me. But I'm not aware of any functionality issues with this currently...
			{
				Control.Size = InterfaceControl.Size;
				return true;
			}
			else
			{
				return false;
			}
		}
				
		private void ReconnectForResize()
		{
			if (_rdpVersion < RdpVersion.RDC80)
			{
				return;
			}
					
			if (!_loginComplete)
			{
				return;
			}
					
			if (!InterfaceControl.Info.AutomaticResize)
			{
				return;
			}
					
			if (!(InterfaceControl.Info.Resolution == RdpResolutions.FitToWindow | InterfaceControl.Info.Resolution == RdpResolutions.Fullscreen))
			{
				return;
			}
					
			if (SmartSize)
			{
				return;
			}

		    var size = !Fullscreen ? Control.Size : Screen.FromControl(Control).Bounds.Size;

            IMsRdpClient8 msRdpClient8 = _rdpClient;
			msRdpClient8.Reconnect((uint)size.Width, (uint)size.Height);
		}
				
		private void SetRdGateway()
		{
			try
			{
				if (_rdpClient.TransportSettings.GatewayIsSupported == 0)
				{
					Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.strRdpGatewayNotSupported, true);
					return;
				}
			    Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.strRdpGatewayIsSupported, true);

			    if (_connectionInfo.RDGatewayUsageMethod != RDGatewayUsageMethod.Never)
				{
					_rdpClient.TransportSettings.GatewayUsageMethod = (uint)_connectionInfo.RDGatewayUsageMethod;
					_rdpClient.TransportSettings.GatewayHostname = _connectionInfo.RDGatewayHostname;
					_rdpClient.TransportSettings.GatewayProfileUsageMethod = 1; // TSC_PROXY_PROFILE_MODE_EXPLICIT
					if (_connectionInfo.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.SmartCard)
					{
						_rdpClient.TransportSettings.GatewayCredsSource = 1; // TSC_PROXY_CREDS_MODE_SMARTCARD
					}
					if (_rdpVersion >= RdpVersion.RDC61 && (Force & ConnectionInfo.Force.NoCredentials) != ConnectionInfo.Force.NoCredentials)
					{
						if (_connectionInfo.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.Yes)
						{
						    _rdpClient.TransportSettings2.GatewayUsername = _connectionInfo.Username;
						    _rdpClient.TransportSettings2.GatewayPassword = _connectionInfo.Password;
						    _rdpClient.TransportSettings2.GatewayDomain = _connectionInfo?.Domain;
						}
						else if (_connectionInfo.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.SmartCard)
						{
							_rdpClient.TransportSettings2.GatewayCredSharing = 0;
						}
						else
						{
                            _rdpClient.TransportSettings2.GatewayUsername = _connectionInfo.RDGatewayUsername;
                            _rdpClient.TransportSettings2.GatewayPassword = _connectionInfo.RDGatewayPassword;
                            _rdpClient.TransportSettings2.GatewayDomain = _connectionInfo.RDGatewayDomain;
                            _rdpClient.TransportSettings2.GatewayCredSharing = 0;
                        }
					}
				}
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetGatewayFailed, ex);
			}
		}
				
		private void SetUseConsoleSession()
		{
			try
			{
				bool value;
						
				if ((Force & ConnectionInfo.Force.UseConsoleSession) == ConnectionInfo.Force.UseConsoleSession)
				{
					value = true;
				}
				else if ((Force & ConnectionInfo.Force.DontUseConsoleSession) == ConnectionInfo.Force.DontUseConsoleSession)
				{
					value = false;
				}
				else
				{
					value = _connectionInfo.UseConsoleSession;
				}
						
				if (_rdpVersion >= RdpVersion.RDC61)
				{
					Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, string.Format(Language.strRdpSetConsoleSwitch, _rdpVersion), true);
					_rdpClient.AdvancedSettings7.ConnectToAdministerServer = value;
				}
				else
				{
					Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, string.Format(Language.strRdpSetConsoleSwitch, _rdpVersion) + Environment.NewLine + "No longer supported in this RDP version. Reference: https://msdn.microsoft.com/en-us/library/aa380863(v=vs.85).aspx", true);
                    // ConnectToServerConsole is deprecated
                    //https://msdn.microsoft.com/en-us/library/aa380863(v=vs.85).aspx
                    //_rdpClient.AdvancedSettings2.ConnectToServerConsole = value;
                }
            }
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetConsoleSessionFailed, ex);
			}
		}
				
		private void SetCredentials()
		{
			try
			{
				if ((Force & ConnectionInfo.Force.NoCredentials) == ConnectionInfo.Force.NoCredentials)
				{
					return;
				}

			    var userName = _connectionInfo?.Username ?? "";
			    var password = _connectionInfo?.Password ?? "";
			    var domain = _connectionInfo?.Domain ?? "";
						
				if (string.IsNullOrEmpty(userName))
				{
					if (Settings.Default.EmptyCredentials == "windows")
					{
						_rdpClient.UserName = Environment.UserName;
					}
					else if (Settings.Default.EmptyCredentials == "custom")
					{
						_rdpClient.UserName = Settings.Default.DefaultUsername;
					}
				}
				else
				{
					_rdpClient.UserName = userName;
				}
						
				if (string.IsNullOrEmpty(password))
				{
					if (Settings.Default.EmptyCredentials == "custom")
					{
						if (Settings.Default.DefaultPassword != "")
						{
                            var cryptographyProvider = new LegacyRijndaelCryptographyProvider();
                            _rdpClient.AdvancedSettings2.ClearTextPassword = cryptographyProvider.Decrypt(Settings.Default.DefaultPassword, Runtime.EncryptionKey);
						}
					}
				}
				else
				{
					_rdpClient.AdvancedSettings2.ClearTextPassword = password;
				}
						
				if (string.IsNullOrEmpty(domain))
				{
					if (Settings.Default.EmptyCredentials == "windows")
					{
						_rdpClient.Domain = Environment.UserDomainName;
					}
					else if (Settings.Default.EmptyCredentials == "custom")
					{
						_rdpClient.Domain = Settings.Default.DefaultDomain;
					}
				}
				else
				{
					_rdpClient.Domain = domain;
				}
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetCredentialsFailed, ex);
			}
		}
				
		private void SetResolution()
		{
			try
			{
				if ((Force & ConnectionInfo.Force.Fullscreen) == ConnectionInfo.Force.Fullscreen)
				{
					_rdpClient.FullScreen = true;
                    _rdpClient.DesktopWidth = Screen.FromControl(_frmMain).Bounds.Width;
                    _rdpClient.DesktopHeight = Screen.FromControl(_frmMain).Bounds.Height;
							
					return;
				}
						
				if ((InterfaceControl.Info.Resolution == RdpResolutions.FitToWindow) || (InterfaceControl.Info.Resolution == RdpResolutions.SmartSize))
				{
					_rdpClient.DesktopWidth = InterfaceControl.Size.Width;
					_rdpClient.DesktopHeight = InterfaceControl.Size.Height;

				    if (InterfaceControl.Info.Resolution == RdpResolutions.SmartSize)
				    {
                        _rdpClient.AdvancedSettings2.SmartSizing = true;
                    }

                    
				}
				else if (InterfaceControl.Info.Resolution == RdpResolutions.Fullscreen)
				{
					_rdpClient.FullScreen = true;
                    _rdpClient.DesktopWidth = Screen.FromControl(_frmMain).Bounds.Width;
                    _rdpClient.DesktopHeight = Screen.FromControl(_frmMain).Bounds.Height;
				}
				else
				{
					var resolution = GetResolutionRectangle(_connectionInfo.Resolution);
					_rdpClient.DesktopWidth = resolution.Width;
					_rdpClient.DesktopHeight = resolution.Height;
				}
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetResolutionFailed, ex);
			}
		}
				
		private void SetPort()
		{
			try
			{
				if (_connectionInfo.Port != (int)Defaults.Port)
				{
					_rdpClient.AdvancedSettings2.RDPPort = _connectionInfo.Port;
				}
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetPortFailed, ex);
			}
		}
				
		private void SetRedirection()
		{
			try
			{
				_rdpClient.AdvancedSettings2.RedirectDrives = _connectionInfo.RedirectDiskDrives;
				_rdpClient.AdvancedSettings2.RedirectPorts = _connectionInfo.RedirectPorts;
				_rdpClient.AdvancedSettings2.RedirectPrinters = _connectionInfo.RedirectPrinters;
				_rdpClient.AdvancedSettings2.RedirectSmartCards = _connectionInfo.RedirectSmartCards;
				_rdpClient.SecuredSettings2.AudioRedirectionMode = (int)_connectionInfo.RedirectSound;
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetRedirectionFailed, ex);
			}
		}
				
		private void SetPerformanceFlags()
		{
			try
			{
				var pFlags = 0;
				if (_connectionInfo.DisplayThemes == false)
				{
					pFlags += Convert.ToInt32(RdpPerformanceFlags.DisableThemes);
				}
						
				if (_connectionInfo.DisplayWallpaper == false)
				{
					pFlags += Convert.ToInt32(RdpPerformanceFlags.DisableWallpaper);
				}
						
				if (_connectionInfo.EnableFontSmoothing)
				{
					pFlags += Convert.ToInt32(RdpPerformanceFlags.EnableFontSmoothing);
				}
						
				if (_connectionInfo.EnableDesktopComposition)
				{
					pFlags += Convert.ToInt32(RdpPerformanceFlags.EnableDesktopComposition);
				}
						
				_rdpClient.AdvancedSettings2.PerformanceFlags = pFlags;
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetPerformanceFlagsFailed, ex);
			}
		}
				
		private void SetAuthenticationLevel()
		{
			try
			{
				_rdpClient.AdvancedSettings5.AuthenticationLevel = (uint)_connectionInfo.RDPAuthenticationLevel;
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetAuthenticationLevelFailed, ex);
			}
		}
				
		private void SetLoadBalanceInfo()
		{
			if (string.IsNullOrEmpty(_connectionInfo.LoadBalanceInfo))
			{
				return;
			}
			try
			{
			    _rdpClient.AdvancedSettings2.LoadBalanceInfo = LoadBalanceInfoUseUtf8
                    ? new AzureLoadBalanceInfoEncoder().Encode(_connectionInfo.LoadBalanceInfo) 
                    : _connectionInfo.LoadBalanceInfo;
			}
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace("Unable to set load balance info.", ex);
			}
		}
				
		private void SetEventHandlers()
		{
			try
			{
				_rdpClient.OnConnecting += RDPEvent_OnConnecting;
				_rdpClient.OnConnected += RDPEvent_OnConnected;
				_rdpClient.OnLoginComplete += RDPEvent_OnLoginComplete;
				_rdpClient.OnFatalError += RDPEvent_OnFatalError;
				_rdpClient.OnDisconnected += RDPEvent_OnDisconnected;
				_rdpClient.OnLeaveFullScreenMode += RDPEvent_OnLeaveFullscreenMode;
                _rdpClient.OnIdleTimeoutNotification += RDPEvent_OnIdleTimeoutNotification;
            }
			catch (Exception ex)
			{
				Runtime.MessageCollector.AddExceptionStackTrace(Language.strRdpSetEventHandlersFailed, ex);
			}
		}
        #endregion
		
        #region Private Events & Handlers
        private void RDPEvent_OnIdleTimeoutNotification()
        {
            Close(); //Simply close the RDP Session if the idle timeout has been triggered.

            if (_alertOnIdleDisconnect)
            {
                string message = "The " + _connectionInfo.Name + " session was disconnected due to inactivity";
                const string caption = "Session Disconnected";
                MessageBox.Show(message, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void RDPEvent_OnFatalError(int errorCode)
		{
			Event_ErrorOccured(this, Convert.ToString(errorCode));
		}
				
		private void RDPEvent_OnDisconnected(int discReason)
		{
			const int UI_ERR_NORMAL_DISCONNECT = 0xB08;
			if (discReason != UI_ERR_NORMAL_DISCONNECT)
			{
				var reason = _rdpClient.GetErrorDescription((uint)discReason, (uint) _rdpClient.ExtendedDisconnectReason);
				Event_Disconnected(this, discReason + "\r\n" + reason);
			}
					
			if (Settings.Default.ReconnectOnDisconnect)
			{
				ReconnectGroup = new ReconnectGroup();
				ReconnectGroup.CloseClicked += Event_ReconnectGroupCloseClicked;
				ReconnectGroup.Left = (int) ((double) Control.Width / 2 - (double) ReconnectGroup.Width / 2);
				ReconnectGroup.Top = (int) ((double) Control.Height / 2 - (double) ReconnectGroup.Height / 2);
				ReconnectGroup.Parent = Control;
				ReconnectGroup.Show();
				tmrReconnect.Enabled = true;
			}
			else
			{
				Close();
			}
		}
				
		private void RDPEvent_OnConnecting()
		{
			Event_Connecting(this);
		}
				
		private void RDPEvent_OnConnected()
		{
			Event_Connected(this);
		}
				
		private void RDPEvent_OnLoginComplete()
		{
			_loginComplete = true;
		}
				
		private void RDPEvent_OnLeaveFullscreenMode()
		{
			Fullscreen = false;
			LeaveFullscreen?.Invoke(this, new EventArgs());
        }
        #endregion
		
		public delegate void LeaveFullscreenEventHandler(object sender, EventArgs e);
		public event LeaveFullscreenEventHandler LeaveFullscreen;
		
		public enum Defaults
		{
			Colors = RdpColors.Colors16Bit,
			Sounds = RdpSounds.DoNotPlay,
			Resolution = RdpResolutions.FitToWindow,
			Port = 3389
		}
		
        #region Resolution
		public static Rectangle GetResolutionRectangle(RdpResolutions resolution)
		{
			string[] resolutionParts = null;
			if (resolution != RdpResolutions.FitToWindow & resolution != RdpResolutions.Fullscreen & resolution != RdpResolutions.SmartSize)
			{
				resolutionParts = resolution.ToString().Replace("Res", "").Split('x');
			}
			if (resolutionParts == null || resolutionParts.Length != 2)
			{
				return new Rectangle(0, 0, 0, 0);
			}
            return new Rectangle(0, 0, Convert.ToInt32(resolutionParts[0]), Convert.ToInt32(resolutionParts[1]));
		}
		#endregion
		
        #region Reconnect Stuff
		public void tmrReconnect_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
		{
			var srvReady = PortScanner.IsPortOpen(_connectionInfo.Hostname, Convert.ToString(_connectionInfo.Port));
					
			ReconnectGroup.ServerReady = srvReady;
					
			if (ReconnectGroup.ReconnectWhenReady && srvReady)
			{
				tmrReconnect.Enabled = false;
				ReconnectGroup.DisposeReconnectGroup();
				//SetProps()
				_rdpClient.Connect();
			}
		}
        #endregion
	}
}
