﻿using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

using Windows.Devices.Enumeration;
using Microsoft.Maker.RemoteWiring;
using Microsoft.Maker.Serial;
using Waher.Content;
using Waher.Content.Xml;
using Waher.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.BitsOfBinary;
using Waher.Networking.XMPP.Chat;
using Waher.Networking.XMPP.Concentrator;
using Waher.Networking.XMPP.PEP;
using Waher.Networking.XMPP.Provisioning;
using Waher.Networking.XMPP.ServiceDiscovery;
using Waher.Networking.XMPP.Sensor;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Persistence.Filters;
using Waher.Persistence.Serialization;
using Waher.Runtime.Inventory;
using Waher.Runtime.Language;
using Waher.Runtime.Settings;
using Waher.Security;
using Waher.Things.SensorData;

using ConcentratorXmpp.History;
using ConcentratorXmpp.Topology;
using Waher.Networking.XMPP.Events;

namespace ConcentratorXmpp
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class.
	/// </summary>
	sealed partial class App : Application
	{
		private static App instance = null;
		private FilesProvider db = null;
		private UsbSerial arduinoUsb = null;
		private RemoteDevice arduino = null;
		private Timer sampleTimer = null;
		private XmppClient xmppClient = null;
		private PepClient pepClient = null;
		private bool newXmppClient = true;

		private const int windowSize = 10;
		private const int spikePos = windowSize / 2;
		private readonly int?[] windowA0 = new int?[windowSize];
		private int nrA0 = 0;
		private int sumA0 = 0;

		private int? lastMinute = null;
		private double? minLight = null;
		private double? maxLight = null;
		private double sumLight = 0;
		private int sumMotion = 0;
		private int nrTerms = 0;
		private DateTime minLightAt = DateTime.MinValue;
		private DateTime maxLightAt = DateTime.MinValue;
		private string deviceId;
		private double? lastLight = null;
		private bool? lastMotion = null;
		private ConcentratorServer concentratorServer = null;
		private ThingRegistryClient registryClient = null;
		private ProvisioningClient provisioningClient = null;
		private BobClient bobClient = null;
		private ChatServer chatServer = null;
		private DateTime lastPublished = DateTime.MinValue;
		private double? lastPublishedLight = null;
		private bool? output = null;

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			this.InitializeComponent();
			this.Suspending += this.OnSuspending;
		}

		public static App Instance => instance;
		public double? Light => this.lastLight;
		public bool? Motion => this.lastMotion;
		public bool? Output => this.output;
		public XmppClient XmppClient => this.xmppClient;
		public string DeviceId => this.deviceId;

		/// <summary>
		/// Invoked when the application is launched normally by the end user.  Other entry points
		/// will be used such as when the application is launched to open a specific file.
		/// </summary>
		/// <param name="e">Details about the launch request and process.</param>
		protected override void OnLaunched(LaunchActivatedEventArgs e)
		{
			// Do not repeat app initialization when the Window already has content,
			// just ensure that the window is active
			if (!(Window.Current.Content is Frame rootFrame))
			{
				// Create a Frame to act as the navigation context and navigate to the first page
				rootFrame = new Frame();

				rootFrame.NavigationFailed += this.OnNavigationFailed;

				if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
				{
					//TODO: Load state from previously suspended application
				}

				// Place the frame in the current Window
				Window.Current.Content = rootFrame;
			}

			if (e.PrelaunchActivated == false)
			{
				if (rootFrame.Content is null)
				{
					// When the navigation stack isn't restored navigate to the first page,
					// configuring the new page by passing required information as a navigation
					// parameter
					rootFrame.Navigate(typeof(MainPage), e.Arguments);
				}
				// Ensure the current window is active
				Window.Current.Activate();
				Task.Run((Action)this.Init);
			}
		}

		private async void Init()
		{
			try
			{
				// Exception types that are logged with an elevated type.
				Log.RegisterAlertExceptionType(true,
					typeof(OutOfMemoryException),
					typeof(StackOverflowException),
					typeof(AccessViolationException),
					typeof(InsufficientMemoryException));

				Log.Informational("Starting application.");

				instance = this;

				Types.Initialize(
					typeof(FilesProvider).GetTypeInfo().Assembly,
					typeof(ObjectSerializer).GetTypeInfo().Assembly,    // Waher.Persistence.Serialization was broken out of Waher.Persistence.FilesLW after the publishing of the MIoT book.
					typeof(RuntimeSettings).GetTypeInfo().Assembly,
					typeof(Translator).GetTypeInfo().Assembly,
					typeof(IContentEncoder).GetTypeInfo().Assembly,
					typeof(XmppClient).GetTypeInfo().Assembly,
					typeof(Waher.Content.Markdown.MarkdownDocument).GetTypeInfo().Assembly,
					typeof(XML).GetTypeInfo().Assembly,
					typeof(Waher.Script.Expression).GetTypeInfo().Assembly,
					typeof(Waher.Script.Graphs.Graph).GetTypeInfo().Assembly,
					typeof(Waher.Script.Persistence.SQL.Select).GetTypeInfo().Assembly,
					typeof(App).GetTypeInfo().Assembly);

				this.db = await FilesProvider.CreateAsync(Windows.Storage.ApplicationData.Current.LocalFolder.Path +
					Path.DirectorySeparatorChar + "Data", "Default", 8192, 1000, 8192, Encoding.UTF8, 10000);
				Database.Register(this.db);
				await this.db.RepairIfInproperShutdown(null);
				await this.db.Start();

				DeviceInformationCollection Devices = await UsbSerial.listAvailableDevicesAsync();
				DeviceInformation DeviceInfo = this.FindDevice(Devices, "Arduino", "USB Serial Device");
				if (DeviceInfo is null)
					Log.Error("Unable to find Arduino device.");
				else
				{
					Log.Informational("Connecting to " + DeviceInfo.Name);

					this.arduinoUsb = new UsbSerial(DeviceInfo);
					this.arduinoUsb.ConnectionEstablished += () =>
						Log.Informational("USB connection established.");

					this.arduino = new RemoteDevice(this.arduinoUsb);
					this.arduino.DeviceReady += async () =>
					{
						try
						{
							Log.Informational("Device ready.");

							this.arduino.pinMode(13, PinMode.OUTPUT);    // Onboard LED.
							this.arduino.digitalWrite(13, PinState.HIGH);

							this.arduino.pinMode(8, PinMode.INPUT);      // PIR sensor (motion detection).
							PinState Pin8 = this.arduino.digitalRead(8);
							this.lastMotion = Pin8 == PinState.HIGH;
							MainPage.Instance.DigitalPinUpdated(8, Pin8);

							this.arduino.pinMode(9, PinMode.OUTPUT);     // Relay.

							this.output = await RuntimeSettings.GetAsync("Actuator.Output", false);
							this.arduino.digitalWrite(9, this.output.Value ? PinState.HIGH : PinState.LOW);

							await MainPage.Instance.OutputSet(this.output.Value);

							Log.Informational("Setting Control Parameter.", string.Empty, "Startup",
								new KeyValuePair<string, object>("Output", this.output));

							this.arduino.pinMode("A0", PinMode.ANALOG); // Light sensor.
							MainPage.Instance.AnalogPinUpdated("A0", this.arduino.analogRead("A0"));

							this.sampleTimer = new Timer(this.SampleValues, null, 1000 - DateTime.Now.Millisecond, 1000);
						}
						catch (Exception ex)
						{
							Log.Exception(ex);
						}
					};

					this.arduino.AnalogPinUpdated += (pin, value) =>
					{
						MainPage.Instance.AnalogPinUpdated(pin, value);
					};

					this.arduino.DigitalPinUpdated += (pin, value) =>
					{
						MainPage.Instance.DigitalPinUpdated(pin, value);

						if (pin == 8)
						{
							bool Motion = (value == PinState.HIGH);
							if (!this.lastMotion.HasValue || (this.lastMotion.Value != Motion))
							{
								this.lastMotion = Motion;
								this.PublishMomentaryValues();

								this.concentratorServer?.NewMomentaryValues(MeteringTopology.SensorNode,
									new BooleanField(MeteringTopology.SensorNode, this.lastPublished, "Motion", Motion,
										FieldType.Momentary, FieldQoS.AutomaticReadout));
							}
						}
					};

					this.arduinoUsb.ConnectionFailed += message =>
					{
						Log.Error("USB connection failed: " + message);
					};

					this.arduinoUsb.ConnectionLost += message =>
					{
						Log.Error("USB connection lost: " + message);
					};

					this.arduinoUsb.begin(57600, SerialConfig.SERIAL_8N1);
				}

				this.deviceId = await RuntimeSettings.GetAsync("DeviceId", string.Empty);
				if (string.IsNullOrEmpty(this.deviceId))
				{
					this.deviceId = Guid.NewGuid().ToString().Replace("-", string.Empty);
					await RuntimeSettings.SetAsync("DeviceId", this.deviceId);
				}

				Log.Informational("Device ID: " + this.deviceId);

				string Host = await RuntimeSettings.GetAsync("XmppHost", "waher.se");
				int Port = (int)await RuntimeSettings.GetAsync("XmppPort", 5222);
				string UserName = await RuntimeSettings.GetAsync("XmppUserName", string.Empty);
				string PasswordHash = await RuntimeSettings.GetAsync("XmppPasswordHash", string.Empty);
				string PasswordHashMethod = await RuntimeSettings.GetAsync("XmppPasswordHashMethod", string.Empty);

				if (string.IsNullOrEmpty(Host) ||
					Port <= 0 || Port > ushort.MaxValue ||
					string.IsNullOrEmpty(UserName) ||
					string.IsNullOrEmpty(PasswordHash) ||
					string.IsNullOrEmpty(PasswordHashMethod))
				{
					await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
						async () => await this.ShowConnectionDialog(Host, Port, UserName));
				}
				else
				{
					this.xmppClient = new XmppClient(Host, Port, UserName, PasswordHash, PasswordHashMethod, "en",
						typeof(App).GetTypeInfo().Assembly)     // Add "new LogSniffer()" to the end, to output communication to the log.
					{
						AllowCramMD5 = false,
						AllowDigestMD5 = false,
						AllowPlain = false,
						AllowScramSHA1 = true,
						AllowScramSHA256 = true
					};
					this.xmppClient.OnStateChanged += this.StateChanged;
					this.xmppClient.OnConnectionError += this.ConnectionError;

					Log.Informational("Connecting to " + this.xmppClient.Host + ":" + this.xmppClient.Port.ToString());
					await this.xmppClient.Connect();
				}
			}
			catch (Exception ex)
			{
				Log.Emergency(ex);

				MessageDialog Dialog = new MessageDialog(ex.Message, "Error");
				await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
					async () => await Dialog.ShowAsync());
			}
		}

		private DeviceInformation FindDevice(DeviceInformationCollection Devices, params string[] DeviceNames)
		{
			foreach (string DeviceName in DeviceNames)
			{
				foreach (DeviceInformation DeviceInfo in Devices)
				{
					if (DeviceInfo.IsEnabled && DeviceInfo.Name.StartsWith(DeviceName))
						return DeviceInfo;
				}
			}

			return null;
		}

		private async Task ShowConnectionDialog(string Host, int Port, string UserName)
		{
			try
			{
				AccountDialog Dialog = new AccountDialog(Host, Port, UserName);

				switch (await Dialog.ShowAsync())
				{
					case ContentDialogResult.Primary:
						if (this.xmppClient != null)
						{
							await this.xmppClient.DisposeAsync();
							this.xmppClient = null;
						}

						this.xmppClient = new XmppClient(Dialog.Host, Dialog.Port, Dialog.UserName, Dialog.Password, "en", typeof(App).GetTypeInfo().Assembly)
						{
							AllowCramMD5 = false,
							AllowDigestMD5 = false,
							AllowPlain = false,
							AllowScramSHA1 = true,
							AllowScramSHA256 = true
						};

						this.xmppClient.AllowRegistration();                // Allows registration on servers that do not require signatures.
																			// this.xmppClient.AllowRegistration(Key, Secret);	// Allows registration on servers requiring a signature of the registration request.

						this.xmppClient.OnStateChanged += this.TestConnectionStateChanged;
						this.xmppClient.OnConnectionError += this.ConnectionError;

						Log.Informational("Connecting to " + this.xmppClient.Host + ":" + this.xmppClient.Port.ToString());
						await this.xmppClient.Connect();
						break;

					case ContentDialogResult.Secondary:
						break;
				}
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
			}
		}

		private Task StateChanged(object _, XmppState State)
		{
			Log.Informational("Changing state: " + State.ToString());

			if (State == XmppState.Connected)
			{
				Log.Informational("Connected as " + this.xmppClient.FullJID);
				Task.Run(this.SetVCard);
				Task.Run(this.RegisterDevice);
			}

			return Task.CompletedTask;
		}

		private Task ConnectionError(object _, Exception ex)
		{
			Log.Error(ex.Message);
			return Task.CompletedTask;
		}

		private async Task AttachFeatures()
		{
			if (this.concentratorServer != null)
			{
				this.concentratorServer.Dispose();
				this.concentratorServer = null;
			}

			this.concentratorServer = await ConcentratorServer.Create(this.xmppClient, this.registryClient, this.provisioningClient, new MeteringTopology());

			if (this.newXmppClient)
			{
				this.newXmppClient = false;

				this.xmppClient.OnError += (Sender, ex) =>
				{
					Log.Error(ex);
					return Task.CompletedTask;
				};

				this.xmppClient.OnPasswordChanged += (Sender, e) =>
				{
					Log.Informational("Password changed.", this.xmppClient.BareJID);
					return Task.CompletedTask;
				};

				this.xmppClient.OnPresenceSubscribed += (Sender, e) =>
				{
					Log.Informational("Friendship request accepted.", this.xmppClient.BareJID, e.From);
					return Task.CompletedTask;
				};

				this.xmppClient.OnPresenceUnsubscribed += (Sender, e) =>
				{
					Log.Informational("Friendship removal accepted.", this.xmppClient.BareJID, e.From);
					return Task.CompletedTask;
				};

				if (this.bobClient is null)
					this.bobClient = new BobClient(this.xmppClient, Path.Combine(Path.GetTempPath(), "BitsOfBinary"));

				this.pepClient?.Dispose();
				this.pepClient = null;

				this.chatServer?.Dispose();
				this.chatServer = null;

				this.chatServer = new ChatServer(this.xmppClient, this.bobClient, this.concentratorServer, this.provisioningClient);
				this.pepClient = new PepClient(this.xmppClient);

				// XEP-0054: vcard-temp: http://xmpp.org/extensions/xep-0054.html
				this.xmppClient.RegisterIqGetHandler("vCard", "vcard-temp", this.QueryVCardHandler, true);
			}
		}

		private void PublishMomentaryValues()
		{
			if (this.xmppClient is null || this.xmppClient.State != XmppState.Connected || !this.lastLight.HasValue || !this.lastMotion.HasValue)
				return;

			/* Three methods to publish data using the Publish/Subscribe pattern exists:
			 * 
			 * 1) Using the presence stanza. In this chase, simply include the sensor data XML when you set your online presence:
			 * 
			 *       this.xmppClient.SetPresence(Availability.Chat, Xml.ToString());
			 *       
			 * 2) Using the Publish/Subscribe extension XEP-0060. In this case, you need to use an XMPP broker that supports XEP-0060, and
			 *    publish the sensor data XML to a node on the broke pubsub service. A subcriber receives the sensor data after successfully
			 *    subscribing to the node. You can use Waher.Networking.XMPP.PubSub to publish data using XEP-0060.
			 *    
			 * 3) Using the Personal Eventing Protocol (PEP) extension XEP-0163. It's a simplified Publish/Subscribe extension where each
			 *    account becomes its own Publish/Subsribe service. A contact with a presence subscription to the sensor, and showing an interest
			 *    in sensor data in its entity capabilities, will receive the data automatically. There's no need to manually subscibe to the data.
			 *    This approach is used in this example. You can use Waher.Networking.XMPP.PEP to publish data using XEP-0163.
			 *    
			 *    To receive personal events, register a personal event handler on the controller. This adds the required namespace to the
			 *    entity capability features list, and the broker will forward the events to you. Example:
			 *    
			 *       this.pepClient.RegisterHandler(typeof(SensorData), PepClient_SensorData);
			 *    
			 *    Then you process the incoming event as follows:
			 *    
			 *       private void PepClient_SensorData(object Sender, PersonalEventNotificationEventArgs e)
			 *       {
			 *          if (e.PersonalEvent is SensorData SensorData)
			 *          {
			 *             ...
			 *          }
			 *       }
			 */

			DateTime Now = DateTime.Now;

			this.pepClient?.Publish(new SensorData(
				new QuantityField(MeteringTopology.SensorNode, Now, "Light", this.lastLight.Value, 2, "%", FieldType.Momentary, FieldQoS.AutomaticReadout),
				new BooleanField(MeteringTopology.SensorNode, Now, "Motion", this.lastMotion.Value, FieldType.Momentary, FieldQoS.AutomaticReadout)), null, null);

			this.lastPublished = Now;
			this.lastPublishedLight = this.lastLight.Value;
		}

		private async Task TestConnectionStateChanged(object Sender, XmppState State)
		{
			Log.Informational("Changing state: " + State.ToString());

			switch (State)
			{
				case XmppState.Connected:
					await RuntimeSettings.SetAsync("XmppHost", this.xmppClient.Host);
					await RuntimeSettings.SetAsync("XmppPort", this.xmppClient.Port);
					await RuntimeSettings.SetAsync("XmppUserName", this.xmppClient.UserName);
					await RuntimeSettings.SetAsync("XmppPasswordHash", this.xmppClient.PasswordHash);
					await RuntimeSettings.SetAsync("XmppPasswordHashMethod", this.xmppClient.PasswordHashMethod);

					this.xmppClient.OnStateChanged -= this.TestConnectionStateChanged;
					this.xmppClient.OnStateChanged += this.StateChanged;
					await this.SetVCard();
					await this.RegisterDevice();
					break;

				case XmppState.Error:
				case XmppState.Offline:
					if (!(this.xmppClient is null))
					{
						await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
							async () => await this.ShowConnectionDialog(this.xmppClient.Host, this.xmppClient.Port, this.xmppClient.UserName));
					}
					break;
			}
		}

		private async Task QueryVCardHandler(object Sender, IqEventArgs e)
		{
			await e.IqResult(await this.GetVCardXml());
		}

		private async Task SetVCard()
		{
			Log.Informational("Setting vCard");

			// XEP-0054 - vcard-temp: http://xmpp.org/extensions/xep-0054.html

			await this.xmppClient.SendIqSet(string.Empty, await this.GetVCardXml(), (sender, e) =>
			{
				if (e.Ok)
					Log.Informational("vCard successfully set.");
				else
					Log.Error("Unable to set vCard.");

				return Task.CompletedTask;

			}, null);
		}

		private async Task<string> GetVCardXml()
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<vCard xmlns='vcard-temp'>");
			Xml.Append("<FN>MIoT Concentrator</FN><N><FAMILY>Concentrator</FAMILY><GIVEN>MIoT</GIVEN><MIDDLE/></N>");
			Xml.Append("<URL>https://github.com/PeterWaher/MIoT</URL>");
			Xml.Append("<JABBERID>");
			Xml.Append(XML.Encode(this.xmppClient.BareJID));
			Xml.Append("</JABBERID>");
			Xml.Append("<UID>");
			Xml.Append(this.deviceId);
			Xml.Append("</UID>");
			Xml.Append("<DESC>XMPP Concentrator Project (ConcentratorXmpp) from the book Mastering Internet of Things, by Peter Waher.</DESC>");

			// XEP-0153 - vCard-Based Avatars: http://xmpp.org/extensions/xep-0153.html

			StorageFile File = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/LargeTile.scale-100.png"));
			byte[] Icon = System.IO.File.ReadAllBytes(File.Path);

			Xml.Append("<PHOTO><TYPE>image/png</TYPE><BINVAL>");
			Xml.Append(Convert.ToBase64String(Icon));
			Xml.Append("</BINVAL></PHOTO>");
			Xml.Append("</vCard>");

			return Xml.ToString();
		}

		private async void SampleValues(object State)
		{
			try
			{
				DateTime Timestamp = DateTime.Now;
				ushort A0 = this.arduino.analogRead("A0");
				PinState D8 = this.arduino.digitalRead(8);

				if (this.windowA0[0].HasValue)
				{
					this.sumA0 -= this.windowA0[0].Value;
					this.nrA0--;
				}

				Array.Copy(this.windowA0, 1, this.windowA0, 0, windowSize - 1);
				this.windowA0[windowSize - 1] = A0;
				this.sumA0 += A0;
				this.nrA0++;

				double AvgA0 = ((double)this.sumA0) / this.nrA0;
				int? v;

				if (this.nrA0 >= windowSize - 2)
				{
					int NrLt = 0;
					int NrGt = 0;

					foreach (int? Value in this.windowA0)
					{
						if (Value.HasValue)
						{
							if (Value.Value < AvgA0)
								NrLt++;
							else if (Value.Value > AvgA0)
								NrGt++;
						}
					}

					if (NrLt == 1 || NrGt == 1)
					{
						v = this.windowA0[spikePos];

						if (v.HasValue)
						{
							if ((NrLt == 1 && v.Value < AvgA0) || (NrGt == 1 && v.Value > AvgA0))
							{
								this.sumA0 -= v.Value;
								this.nrA0--;
								this.windowA0[spikePos] = null;

								AvgA0 = ((double)this.sumA0) / this.nrA0;

								Log.Informational("Spike removed.", new KeyValuePair<string, object>("A0", v.Value));
							}
						}
					}
				}

				int i, n;

				for (AvgA0 = i = n = 0; i < spikePos; i++)
				{
					if ((v = this.windowA0[i]).HasValue)
					{
						n++;
						AvgA0 += v.Value;
					}
				}

				if (n > 0)
				{
					AvgA0 /= n;
					double Light = (100.0 * AvgA0) / 1024;
					this.lastLight = Light;
					MainPage.Instance.LightUpdated(Light, 2, "%");

					this.sumLight += Light;
					this.sumMotion += (D8 == PinState.HIGH ? 1 : 0);
					this.nrTerms++;

					this.concentratorServer?.NewMomentaryValues(MeteringTopology.SensorNode,
						new QuantityField(MeteringTopology.SensorNode, Timestamp, "Light", Light, 2, "%",
							FieldType.Momentary, FieldQoS.AutomaticReadout));

					if (!this.minLight.HasValue || Light < this.minLight.Value)
					{
						this.minLight = Light;
						this.minLightAt = Timestamp;
					}

					if (!this.maxLight.HasValue || Light > this.maxLight.Value)
					{
						this.maxLight = Light;
						this.maxLightAt = Timestamp;
					}

					double SecondsSinceLast = (Timestamp - this.lastPublished).TotalSeconds;
					if (!this.lastPublishedLight.HasValue ||
						(SecondsSinceLast >= 5 && (Math.Abs(this.lastPublishedLight.Value - Light) >= 1)) ||
						SecondsSinceLast >= 60)
					{
						this.PublishMomentaryValues();
					}

					if (!this.lastMinute.HasValue)
						this.lastMinute = Timestamp.Minute;
					else if (this.lastMinute.Value != Timestamp.Minute)
					{
						this.lastMinute = Timestamp.Minute;

						LastMinute Rec = new LastMinute()
						{
							Timestamp = Timestamp,
							Light = Light,
							Motion = D8,
							MinLight = this.minLight,
							MinLightAt = this.minLightAt,
							MaxLight = this.maxLight,
							MaxLightAt = this.maxLightAt,
							AvgLight = (this.nrTerms == 0 ? (double?)null : this.sumLight / this.nrTerms),
							AvgMotion = (this.nrTerms == 0 ? (double?)null : (this.sumMotion * 100.0) / this.nrTerms)
						};

						await Database.Insert(Rec);

						this.minLight = null;
						this.minLightAt = DateTime.MinValue;
						this.maxLight = null;
						this.maxLightAt = DateTime.MinValue;
						this.sumLight = 0;
						this.sumMotion = 0;
						this.nrTerms = 0;

						foreach (LastMinute Rec2 in await Database.Find<LastMinute>(new FilterFieldLesserThan("Timestamp", Timestamp.AddMinutes(-100))))
							await Database.Delete(Rec2);

						if (Timestamp.Minute == 0)
						{
							DateTime From = new DateTime(Timestamp.Year, Timestamp.Month, Timestamp.Day, Timestamp.Hour, 0, 0).AddHours(-1);
							DateTime To = From.AddHours(1);
							int NLight = 0;
							int NMotion = 0;

							LastHour HourRec = new LastHour()
							{
								Timestamp = Timestamp,
								Light = Light,
								Motion = D8,
								MinLight = Rec.MinLight,
								MinLightAt = Rec.MinLightAt,
								MaxLight = Rec.MaxLight,
								MaxLightAt = Rec.MaxLightAt,
								AvgLight = 0,
								AvgMotion = 0
							};

							foreach (LastMinute Rec2 in await Database.Find<LastMinute>(0, 60, new FilterAnd(
								new FilterFieldLesserThan("Timestamp", To),
								new FilterFieldGreaterOrEqualTo("Timestamp", From))))
							{
								if (Rec2.AvgLight.HasValue)
								{
									HourRec.AvgLight += Rec2.AvgLight.Value;
									NLight++;
								}

								if (Rec2.AvgMotion.HasValue)
								{
									HourRec.AvgMotion += Rec2.AvgMotion.Value;
									NMotion++;
								}

								if (Rec2.MinLight < HourRec.MinLight)
								{
									HourRec.MinLight = Rec2.MinLight;
									HourRec.MinLightAt = Rec.MinLightAt;
								}

								if (Rec2.MaxLight < HourRec.MaxLight)
								{
									HourRec.MaxLight = Rec2.MaxLight;
									HourRec.MaxLightAt = Rec.MaxLightAt;
								}
							}

							if (NLight == 0)
								HourRec.AvgLight = null;
							else
								HourRec.AvgLight /= NLight;

							if (NMotion == 0)
								HourRec.AvgMotion = null;
							else
								HourRec.AvgMotion /= NMotion;

							await Database.Insert(HourRec);

							foreach (LastHour Rec2 in await Database.Find<LastHour>(new FilterFieldLesserThan("Timestamp", Timestamp.AddHours(-100))))
								await Database.Delete(Rec2);

							if (Timestamp.Hour == 0)
							{
								From = new DateTime(Timestamp.Year, Timestamp.Month, Timestamp.Day, 0, 0, 0).AddDays(-1);
								To = From.AddDays(1);
								NLight = 0;
								NMotion = 0;

								LastDay DayRec = new LastDay()
								{
									Timestamp = Timestamp,
									Light = Light,
									Motion = D8,
									MinLight = HourRec.MinLight,
									MinLightAt = HourRec.MinLightAt,
									MaxLight = HourRec.MaxLight,
									MaxLightAt = HourRec.MaxLightAt,
									AvgLight = 0,
									AvgMotion = 0
								};

								foreach (LastHour Rec2 in await Database.Find<LastHour>(0, 24, new FilterAnd(
									new FilterFieldLesserThan("Timestamp", To),
									new FilterFieldGreaterOrEqualTo("Timestamp", From))))
								{
									if (Rec2.AvgLight.HasValue)
									{
										DayRec.AvgLight += Rec2.AvgLight.Value;
										NLight++;
									}

									if (Rec2.AvgMotion.HasValue)
									{
										DayRec.AvgMotion += Rec2.AvgMotion.Value;
										NMotion++;
									}

									if (Rec2.MinLight < DayRec.MinLight)
									{
										DayRec.MinLight = Rec2.MinLight;
										DayRec.MinLightAt = Rec.MinLightAt;
									}

									if (Rec2.MaxLight < DayRec.MaxLight)
									{
										DayRec.MaxLight = Rec2.MaxLight;
										DayRec.MaxLightAt = Rec.MaxLightAt;
									}
								}

								if (NLight == 0)
									DayRec.AvgLight = null;
								else
									DayRec.AvgLight /= NLight;

								if (NMotion == 0)
									DayRec.AvgMotion = null;
								else
									DayRec.AvgMotion /= NMotion;

								await Database.Insert(DayRec);

								foreach (LastDay Rec2 in await Database.Find<LastDay>(new FilterFieldLesserThan("Timestamp", Timestamp.AddDays(-100))))
									await Database.Delete(Rec2);
							}
						}
					}
				}

				if (Timestamp.Second == 0 && this.xmppClient != null &&
					(this.xmppClient.State == XmppState.Error || this.xmppClient.State == XmppState.Offline))
				{
					await this.xmppClient.Reconnect();
				}
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
			}
		}

		internal async Task SetOutput(bool On, string Actor)
		{
			if (this.arduino != null)
			{
				this.arduino.digitalWrite(9, On ? PinState.HIGH : PinState.LOW);

				await RuntimeSettings.SetAsync("Actuator.Output", On);
				this.output = On;

				this.concentratorServer?.NewMomentaryValues(MeteringTopology.ActuatorNode,
					new BooleanField(MeteringTopology.ActuatorNode, DateTime.Now, "Output", On,
						FieldType.Momentary, FieldQoS.AutomaticReadout));

				Log.Informational("Setting Control Parameter.", string.Empty, Actor ?? "Windows user",
					new KeyValuePair<string, object>("Output", On));

				if (Actor != null)
					await MainPage.Instance.OutputSet(On);
			}
		}

		private async Task RegisterDevice()
		{
			string ThingRegistryJid = await RuntimeSettings.GetAsync("ThingRegistry.JID", string.Empty);
			string ProvisioningJid = await RuntimeSettings.GetAsync("ProvisioningServer.JID", ThingRegistryJid);
			string OwnerJid = await RuntimeSettings.GetAsync("ThingRegistry.Owner", string.Empty);

			if (!string.IsNullOrEmpty(ThingRegistryJid) && !string.IsNullOrEmpty(ProvisioningJid))
			{
				await this.UseProvisioningServer(ProvisioningJid, OwnerJid);
				await this.RegisterDevice(ThingRegistryJid, OwnerJid);
			}
			else
			{
				Log.Informational("Searching for Thing Registry and Provisioning Server.");

				await this.xmppClient.SendServiceItemsDiscoveryRequest(this.xmppClient.Domain, (sender, e) =>
				{
					foreach (Item Item in e.Items)
					{
						this.xmppClient.SendServiceDiscoveryRequest(Item.JID, async (sender2, e2) =>
						{
							try
							{
								Item Item2 = (Item)e2.State;

								if (e2.HasAnyFeature(ProvisioningClient.NamespacesProvisioningDevice))
								{
									Log.Informational("Provisioning server found.", Item2.JID);
									await this.UseProvisioningServer(Item2.JID, OwnerJid);
									await RuntimeSettings.SetAsync("ProvisioningServer.JID", Item2.JID);
								}

								if (e2.HasAnyFeature(ThingRegistryClient.NamespacesDiscovery))
								{
									Log.Informational("Thing registry found.", Item2.JID);

									await RuntimeSettings.SetAsync("ThingRegistry.JID", Item2.JID);
									await this.RegisterDevice(Item2.JID, OwnerJid);
								}
							}
							catch (Exception ex)
							{
								Log.Exception(ex);
							}
						}, Item);
					}

					return Task.CompletedTask;

				}, null);
			}
		}

		private async Task UseProvisioningServer(string JID, string OwnerJid)
		{
			if (this.provisioningClient is null ||
				this.provisioningClient.ProvisioningServerAddress != JID ||
				this.provisioningClient.OwnerJid != OwnerJid)
			{
				if (this.provisioningClient != null)
				{
					this.provisioningClient.Dispose();
					this.provisioningClient = null;
				}

				this.provisioningClient = new ProvisioningClient(this.xmppClient, JID, OwnerJid);

				this.provisioningClient.CacheCleared += (sender, e) =>
				{
					Log.Informational("Rule cache cleared.");
					return Task.CompletedTask;
				};

				await this.AttachFeatures();
			}
		}

		private async Task RegisterDevice(string RegistryJid, string OwnerJid)
		{
			if (this.registryClient is null || this.registryClient.ThingRegistryAddress != RegistryJid)
			{
				if (this.registryClient != null)
				{
					this.registryClient.Dispose();
					this.registryClient = null;
				}

				this.registryClient = new ThingRegistryClient(this.xmppClient, RegistryJid);

				this.registryClient.Claimed += async (sender, e) =>
				{
					try
					{
						if (e.Node.IsEmpty)
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Owner", e.JID);
							await RuntimeSettings.SetAsync("ThingRegistry.Key", string.Empty);
						}
						else if (e.Node.Equals(MeteringTopology.SensorNode))
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Sensor.Owner", e.JID);
							await RuntimeSettings.SetAsync("ThingRegistry.Sensor.Key", string.Empty);
						}
						else if (e.Node.Equals(MeteringTopology.ActuatorNode))
						{
							await RuntimeSettings.SetAsync("ThingRegistry.Actuator.Owner", e.JID);
							await RuntimeSettings.SetAsync("ThingRegistry.Actuator.Key", string.Empty);
						}
					}
					catch (Exception ex)
					{
						Log.Exception(ex);
					}
				};

				this.registryClient.Disowned += async (sender, e) =>
				{
					try
					{
						if (e.Node.IsEmpty)
							await RuntimeSettings.SetAsync("ThingRegistry.Owner", string.Empty);
						else if (e.Node.Equals(MeteringTopology.SensorNode))
							await RuntimeSettings.SetAsync("ThingRegistry.Sensor.Owner", string.Empty);
						else if (e.Node.Equals(MeteringTopology.ActuatorNode))
							await RuntimeSettings.SetAsync("ThingRegistry.Actuator.Owner", string.Empty);

						await this.RegisterDevice();
					}
					catch (Exception ex)
					{
						Log.Exception(ex);
					}
				};
			}

			string s;
			List<MetaDataTag> MetaInfo = new List<MetaDataTag>()
			{
				new MetaDataStringTag("MAN", "waher.se"),
				new MetaDataStringTag("MODEL", "MIoT ConcentratorXmpp2"),
				new MetaDataStringTag("PURL", "https://github.com/PeterWaher/MIoT"),
				new MetaDataStringTag("SN", this.deviceId),
				new MetaDataNumericTag("V", 2.0)
			};

			if (await RuntimeSettings.GetAsync("ThingRegistry.Location", false))
			{
				s = await RuntimeSettings.GetAsync("ThingRegistry.Country", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("COUNTRY", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Region", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("REGION", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.City", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("CITY", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Area", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("AREA", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Street", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("STREET", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.StreetNr", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("STREETNR", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Building", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("BLD", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Apartment", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("APT", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Room", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("ROOM", s));

				s = await RuntimeSettings.GetAsync("ThingRegistry.Name", string.Empty);
				if (!string.IsNullOrEmpty(s))
					MetaInfo.Add(new MetaDataStringTag("NAME", s));

				await this.UpdateRegistration(MetaInfo.ToArray(), OwnerJid);
			}
			else
			{
				try
				{
					await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
					{
						try
						{
							RegistrationDialog Dialog = new RegistrationDialog();

							switch (await Dialog.ShowAsync())
							{
								case ContentDialogResult.Primary:
									await RuntimeSettings.SetAsync("ThingRegistry.Country", s = Dialog.Reg_Country);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("COUNTRY", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Region", s = Dialog.Reg_Region);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("REGION", s));

									await RuntimeSettings.SetAsync("ThingRegistry.City", s = Dialog.Reg_City);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("CITY", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Area", s = Dialog.Reg_Area);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("AREA", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Street", s = Dialog.Reg_Street);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("STREET", s));

									await RuntimeSettings.SetAsync("ThingRegistry.StreetNr", s = Dialog.Reg_StreetNr);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("STREETNR", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Building", s = Dialog.Reg_Building);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("BLD", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Apartment", s = Dialog.Reg_Apartment);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("APT", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Room", s = Dialog.Reg_Room);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("ROOM", s));

									await RuntimeSettings.SetAsync("ThingRegistry.Name", s = Dialog.Name);
									if (!string.IsNullOrEmpty(s))
										MetaInfo.Add(new MetaDataStringTag("NAME", s));

									await this.RegisterDevice(MetaInfo.ToArray());
									break;

								case ContentDialogResult.Secondary:
									await this.RegisterDevice();
									break;
							}
						}
						catch (Exception ex)
						{
							Log.Exception(ex);
						}
					});
				}
				catch (Exception ex)
				{
					Log.Exception(ex);
				}
			}
		}

		private async Task RegisterDevice(MetaDataTag[] MetaInfo)
		{
			await this.RegisterDevice(this.GetConcentratorMetaInfo(MetaInfo), string.Empty, string.Empty, string.Empty);
			await this.RegisterDevice(this.GetSensorMetaInfo(MetaInfo), "Sensor.", SensorNode.NodeID, MeteringTopology.ID);
			await this.RegisterDevice(this.GetActuatorMetaInfo(MetaInfo), "Actuator.", ActuatorNode.NodeID, MeteringTopology.ID);
		}

		private async Task RegisterDevice(MetaDataTag[] MetaInfo, string Device, string NodeID, string SourceID)
		{
			string DeviceName = string.IsNullOrEmpty(Device) ? "Concentrator." : Device;

			Log.Informational("Registering " + DeviceName);

			string Key = await RuntimeSettings.GetAsync("ThingRegistry." + Device + "Key", string.Empty);
			if (string.IsNullOrEmpty(Key))
			{
				byte[] Bin = new byte[32];
				using (RandomNumberGenerator Rnd = RandomNumberGenerator.Create())
				{
					Rnd.GetBytes(Bin);
				}

				Key = Hashes.BinaryToString(Bin);
				await RuntimeSettings.SetAsync("ThingRegistry." + Device + "Key", Key);
			}

			int c = MetaInfo.Length;
			Array.Resize<MetaDataTag>(ref MetaInfo, c + 1);
			MetaInfo[c] = new MetaDataStringTag("KEY", Key);

			await this.registryClient.RegisterThing(false, NodeID, SourceID, MetaInfo, async (sender, e) =>
			{
				try
				{
					if (e.Ok)
					{
						await RuntimeSettings.SetAsync("ThingRegistry." + Device + "Location", true);
						await RuntimeSettings.SetAsync("ThingRegistry." + Device + "Owner", e.OwnerJid);

						if (string.IsNullOrEmpty(e.OwnerJid))
						{
							string ClaimUrl = this.registryClient.EncodeAsIoTDiscoURI(MetaInfo);
							string FilePath = ApplicationData.Current.LocalFolder.Path + Path.DirectorySeparatorChar + DeviceName + "iotdisco";

							Log.Informational("Successful registration of " + DeviceName);
							Log.Informational(ClaimUrl, new KeyValuePair<string, object>("Path", FilePath));

							File.WriteAllText(FilePath, ClaimUrl);
						}
						else
						{
							await RuntimeSettings.SetAsync("ThingRegistry." + Device + ".Key", string.Empty);
							Log.Informational("Updated registration of " + DeviceName + " Device has an owner.", new KeyValuePair<string, object>("Owner", e.OwnerJid));
						}
					}
					else
					{
						Log.Error("Failed registration of " + DeviceName, NodeID);
						await this.RegisterDevice();
					}
				}
				catch (Exception ex)
				{
					Log.Exception(ex);
				}
			}, null);
		}

		private MetaDataTag[] GetConcentratorMetaInfo(MetaDataTag[] MetaInfo)
		{
			List<MetaDataTag> ConcentratorTags = new List<MetaDataTag>(MetaInfo)
			{
				new MetaDataStringTag("CLASS", "Concentrator"),
				new MetaDataStringTag("TYPE", "MIoT Concentrator")
			};

			return ConcentratorTags.ToArray();
		}

		private MetaDataTag[] GetSensorMetaInfo(MetaDataTag[] MetaInfo)
		{
			List<MetaDataTag> SensorTags = new List<MetaDataTag>(MetaInfo)
			{
				new MetaDataStringTag("CLASS", "Sensor"),
				new MetaDataStringTag("TYPE", "MIoT Sensor")
			};

			return SensorTags.ToArray();
		}

		private MetaDataTag[] GetActuatorMetaInfo(MetaDataTag[] MetaInfo)
		{
			List<MetaDataTag> ActuatorTags = new List<MetaDataTag>(MetaInfo)
			{
				new MetaDataStringTag("CLASS", "Actuator"),
				new MetaDataStringTag("TYPE", "MIoT Actuator"),
			};

			return ActuatorTags.ToArray();
		}

		private async Task UpdateRegistration(MetaDataTag[] MetaInfo, string OwnerJid)
		{
			if (string.IsNullOrEmpty(OwnerJid))
				await this.RegisterDevice(MetaInfo);
			else
			{

				Log.Informational("Updating registration of device.",
					new KeyValuePair<string, object>("Owner", OwnerJid));

				this.UpdateRegistration(this.GetConcentratorMetaInfo(MetaInfo), string.Empty, string.Empty, string.Empty);
				this.UpdateRegistration(this.GetSensorMetaInfo(MetaInfo), "Sensor.", SensorNode.NodeID, MeteringTopology.ID);
				this.UpdateRegistration(this.GetActuatorMetaInfo(MetaInfo), "Actuator.", ActuatorNode.NodeID, MeteringTopology.ID);
			}
		}

		private void UpdateRegistration(MetaDataTag[] MetaInfo, string Device, string NodeID, string SourceID)
		{
			string DeviceName = string.IsNullOrEmpty(Device) ? "Concentrator." : Device;

			Log.Informational("Updating registration of " + DeviceName);

			this.registryClient.UpdateThing(NodeID, SourceID, MetaInfo, async (sender, e) =>
			{
				try
				{
					if (e.Disowned)
					{
						await RuntimeSettings.SetAsync("ThingRegistry." + Device + "Owner", string.Empty);
						await this.RegisterDevice(MetaInfo);
					}
					else if (e.Ok)
						Log.Informational("Registration update successful.", NodeID);
					else
					{
						Log.Error("Registration update failed.", NodeID);
						await this.RegisterDevice(MetaInfo, Device, NodeID, SourceID);
					}
				}
				catch (Exception ex)
				{
					Log.Exception(ex);
				}
			}, null);
		}

		/// <summary>
		/// Invoked when Navigation to a certain page fails
		/// </summary>
		/// <param name="sender">The Frame which failed navigation</param>
		/// <param name="e">Details about the navigation failure</param>
		void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
		{
			throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
		}

		/// <summary>
		/// Invoked when application execution is being suspended.  Application state is saved
		/// without knowing whether the application will be terminated or resumed with the contents
		/// of memory still intact.
		/// </summary>
		/// <param name="sender">The source of the suspend request.</param>
		/// <param name="e">Details about the suspend request.</param>
		private void OnSuspending(object sender, SuspendingEventArgs e)
		{
			var deferral = e.SuspendingOperation.GetDeferral();

			instance = null;

			this.pepClient?.Dispose();
			this.pepClient = null;

			this.chatServer?.Dispose();
			this.chatServer = null;

			this.bobClient?.Dispose();
			this.bobClient = null;

			this.concentratorServer?.Dispose();
			this.concentratorServer = null;

			this.xmppClient?.DisposeAsync().Wait();
			this.xmppClient = null;

			this.sampleTimer?.Dispose();
			this.sampleTimer = null;

			if (this.arduino != null)
			{
				this.arduino.digitalWrite(13, PinState.LOW);
				this.arduino.pinMode(13, PinMode.INPUT);     // Onboard LED.
				this.arduino.pinMode(9, PinMode.INPUT);      // Relay.

				this.arduino.Dispose();
				this.arduino = null;
			}

			if (this.arduinoUsb != null)
			{
				this.arduinoUsb.end();
				this.arduinoUsb.Dispose();
				this.arduinoUsb = null;
			}

			this.db?.Stop()?.Wait();
			this.db?.Flush()?.Wait();

			Log.TerminateAsync().Wait();

			deferral.Complete();
		}
	}
}
