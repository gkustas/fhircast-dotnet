using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using Nuance.Radiology.Services.Contracts;
using Nuance.RadWhere;
using System.Net.WebSockets;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Collections.Concurrent;

namespace Commissure.Render.PACS
{
	class FHIRCast360 : IPACS, IDisposable
	{
		private string _topic = null;
		private string _siteName;
		private string _multiSite = "";
		private string _hubUrl = "http://localhost:5000";
//		private string _hubUrl = "http://connect.nuancepowerscribe.com";
		private string _secret = "61B584A8-C5AD-4A87-A40F-19E448EEBBAD";
		private IRadWhere _rw = null;
		private RadWhereAxLib.LogWriter _logWriter = new RadWhereAxLib.LogWriter();
		private string _pacsName = PACSType.FHIRCast360.ToString();
		private ClientWebSocket _ws = new ClientWebSocket();
		private BackgroundWorker _webSocketReader;
		private BackgroundWorker _notificationHandler;
		private ConcurrentQueue<Notification> _notificationQueue = new ConcurrentQueue<Notification>();
		private AutoResetEvent _notificationEvent = new AutoResetEvent(false);
		private AutoResetEvent _rwEvent = new AutoResetEvent(false);

		public FHIRCast360(string siteName)
		{
			_siteName = siteName;
		}

#pragma warning disable 0067
		public event SceneAcquiredHandler SceneAcquired;
		public event ExceptionOccurredHandler ExceptionOccurred;
#pragma warning restore 0067
		
		#region RadWhere Event Handlers

		async void _rw_ReportOpened(object sender, ReportEventArgs e)
		{
			_rwEvent.Set();
			_logWriter.Log(1, $"Report opened; accession numbers: {e.AccessionNumbers}");
			string patientGUID = Guid.NewGuid().ToString("N");
			string mrn = _rw.PatientIdentifier;
			Notification notification = new Notification
			{
				Timestamp = DateTime.Now,
				Event = new NotificationEvent()
				{
					HubEvent = HubEventType.OpenImagingStudy,
					Topic = _topic,
					Contexts = new Context[]
					{
						new Context()
						{
							Key = "patient",
							Resource = new Resource()
							{
								ResourceType = "Patient",
								Id = patientGUID,
								Identifiers = new Identifier[]
								{
									new Identifier()
									{
										System = "urn:mrn",
										Value= mrn
									}
								}
							}
						},
						new Context()
						{
							Key = "study",
							Resource = new Resource()
							{
								ResourceType = "ImagingStudy",
								Id = Guid.NewGuid().ToString(),
								Accession = new Identifier //TODO: use R4 spec for identifiers - one for each accession
								{
									System = "urn:accession",
									Value = String.Join(",", e.AccessionNumbers)
								},
								Patient = new ResourceReference
								{
									Reference = $"patient/{patientGUID}"
								}
							}
						}
					}
				}
			};
			string json = notification.ToString();
			await SendStringAsync(_ws, json, CancellationToken.None);
			//TODO: process acknowledgements
		}

		async void _rw_ReportClosed(object sender, ReportEventArgs e)
		{
			_logWriter.Log(1, $"Report closed; accession numbers: {e.AccessionNumbers}");
			_rwEvent.Set();
			string patientGUID = Guid.NewGuid().ToString("N");
			string mrn = _rw.PatientIdentifier;
			Notification notification = new Notification
			{
				Timestamp = DateTime.Now,
				Event = new NotificationEvent()
				{
					HubEvent = HubEventType.CloseImagingStudy,
					Topic = _topic,
					Contexts = new Context[]
					{
						new Context()
						{
							Key = "patient",
							Resource = new Resource()
							{
								ResourceType = "Patient",
								Id = patientGUID,
								Identifiers = new Identifier[]
								{
									new Identifier()
									{
										System = "urn:mrn",
										Value= mrn
									}
								}
							}
						},
						new Context()
						{
							Key = "study",
							Resource = new Resource()
							{
								ResourceType = "ImagingStudy",
								Id = Guid.NewGuid().ToString(),
								Accession = new Identifier //TODO: use R4 spec for identifiers - one for each accession
								{
									System = "urn:accession",
									Value = String.Join(",", e.AccessionNumbers)
								},
								Patient = new ResourceReference
								{
									Reference = $"patient/{patientGUID}"
								}
							}
						}
					}
				}
			};
			string json = notification.ToString();
			await SendStringAsync(_ws, json, CancellationToken.None);
			//TODO: process acknowledgements
		}

		void _rw_UserLoggedIn(object sender, LoginEventArgs e)
		{
			_logWriter.Log(5, String.Format("UserLoggedIn({0})", e.Username));
		}
		#endregion

		#region Private Members

		private async void _webSocketReader_DoWork(object sender, DoWorkEventArgs e)
		{
			while (true)
			{
				string response = null;
				try
				{
					response = await ReceiveStringAsync(_ws, CancellationToken.None);
				}
				catch (Exception ex)
				{
					Debug.WriteLine(ex.ToString());
					//TODO: Handle hub disconnect better than this
					_logWriter.Log(1, "Exception occurred reading from websocket:");
					_logWriter.Log(1, ex.ToString());
					if (null != ex.InnerException)
						_logWriter.Log(1, ex.InnerException.ToString());
					OnErrorOccurred("A problem occurred communicating to the FHIRCast Hub. A new logon will be necessary to re-establish the PACS integration.");
				}
				if (string.IsNullOrEmpty(response))
				{
					if (_ws.State != WebSocketState.Open)
						break;
					continue;
				}
				// it's either an acknowledgement or an event notification...
				//TODO: define better object model for websocket messages. Possible spec change?
				Notification notification = JsonConvert.DeserializeObject<Notification>(response);
				if (null != notification.Event)
				{
					_logWriter.Log(3, $"Event notification received:\r\n {notification.Event}");
					Debug.WriteLine($"Event notification received:\r\n {notification.Event}");
					_notificationQueue.Enqueue(notification); 
					_notificationEvent.Set();
				}
				else if (null != notification.Status)
				{
					_logWriter.Log(3, $"Acknowledgement response received:\r\n{notification.Status}({notification.StatusCode})");
					Debug.WriteLine($"Acknowledgement response received:\r\n{notification.Status}({notification.StatusCode})");
				}
				else
				{
					_logWriter.Log(3, $"Unexpected websocket message received: {response}");
					Debug.WriteLine($"Unexpected websocket message received: {response}");
				}
			}
		}

		private static Task SendStringAsync(WebSocket socket, string data, CancellationToken ct = default(CancellationToken))
		{
			var buffer = Encoding.UTF8.GetBytes(data);
			var segment = new ArraySegment<byte>(buffer);
			return socket.SendAsync(segment, WebSocketMessageType.Text, true, ct);
		}

		private static async Task<string> ReceiveStringAsync(WebSocket socket, CancellationToken ct = default(CancellationToken))
		{
			var buffer = new ArraySegment<byte>(new byte[8192]);
			using (var ms = new MemoryStream())
			{
				WebSocketReceiveResult result;
				do
				{
					ct.ThrowIfCancellationRequested();

					result = await socket.ReceiveAsync(buffer, ct);
					ms.Write(buffer.Array, buffer.Offset, result.Count);
				}
				while (!result.EndOfMessage);
				ms.Seek(0, SeekOrigin.Begin);
				if (result.MessageType != WebSocketMessageType.Text)
				{
					return null;
				}
				using (var reader = new StreamReader(ms, Encoding.UTF8))
				{
					string data = await reader.ReadToEndAsync();
					System.Diagnostics.Debug.WriteLine($"received data: {data}");
					return data;
				}
			}
		}

		private async void Subscribe()
		{
			// Authenticate and obtain topic via REST
			HttpClient client = new HttpClient();
			UriBuilder urlBuilder = new UriBuilder($"{_hubUrl}/api/hub/gettopic?username={_rw.Username}&secret={_secret}");
			HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, urlBuilder.Uri);
			HttpResponseMessage response;
			try
			{
				response = await client.SendAsync(request);
				_topic = await response.Content.ReadAsStringAsync();
				if (String.IsNullOrEmpty(_topic))
				{
					_logWriter.Log(3, $"Validation failed for username: {_rw.Username}, secret: {_secret}");
					Debug.WriteLine($"Validation failed for username: {_rw.Username}, secret: {_secret}");
					return;
				}
			}
			catch (Exception ex)
			{
				_logWriter.Log(3, $"Exception occurred getting topic:\r\n{ex.ToString()}");
				return;
			}
			_logWriter.Log(3, $"Got topic: {_topic}");
			Debug.WriteLine($"Got topic: {_topic}");

			// Subscribe via REST
			urlBuilder = new UriBuilder($"{_hubUrl}/api/hub");
			client = new HttpClient();
			string mode = SubscriptionMode.Subscribe;
			string events = "open-imaging-study,switch-imaging-study,close-imaging-study,logout";
			request = new HttpRequestMessage(HttpMethod.Post, urlBuilder.Uri);
			request.Content = new FormUrlEncodedContent(
				new Dictionary<string, string>
				{
					{ "hub.callback", "" },
					{ "hub.channel.type", "websocket" },
					{ "hub.mode", "subscribe" },
					{ "hub.topic", _topic },
					{ "hub.events", events }
				}
			);
			string responseText = null;
			try
			{
				response = await client.SendAsync(request);
				responseText = await response.Content.ReadAsStringAsync();
				_logWriter.Log(3, $"Subscription response: {responseText}");
				Debug.WriteLine($"Subscription response: {responseText}");
			}
			catch (Exception ex)
			{
				_logWriter.Log(1, "Exception occurred subscribing to Hub:");
				_logWriter.Log(1, ex.ToString());
				Debug.WriteLine("Exception occurred subscribing to Hub:");
				Debug.WriteLine(ex.ToString());
			}
			if (_ws.State != WebSocketState.Open)
			{
				try
				{
					string wsUrl = $"{_hubUrl}/{_topic}".Replace("http", "ws").Replace("https", "wss");
					Debug.WriteLine($"Connecting to Hub Websocket: {wsUrl}");
					_logWriter.Log(5, $"Connecting to Hub Websocket: {wsUrl}");
					try
					{
						await _ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
						string conResponse = await ReceiveStringAsync(_ws, CancellationToken.None);
						_logWriter.Log(5, $"Websocket connection received response:\r\n{conResponse}");
						Debug.WriteLine($"Websocket connection received response:\r\n{conResponse}");
					}
					catch (Exception ex)
					{
						_logWriter.Log(1, "Exception connecting websocket to hub:");
						_logWriter.Log(1, ex.ToString());
						Debug.WriteLine("Exception connecting websocket to hub:");
						Debug.WriteLine(ex.ToString());
						OnErrorOccurred("A problem occurred connecting Powerscribe360 to the FHIRCast Hub. A restart will be necessary to re-establish the PACS integration.");
					}
					// set up background reader
					_webSocketReader = new BackgroundWorker();
					_webSocketReader.DoWork += _webSocketReader_DoWork;
					_webSocketReader.RunWorkerAsync();
					// set up background notification processor
					_notificationHandler = new BackgroundWorker();
					_notificationHandler.DoWork += _notificationHandler_DoWork;
					_notificationHandler.RunWorkerAsync();
				}
				catch (Exception ex)
				{
					_ws.Dispose();
					_ws = new ClientWebSocket();
					_logWriter.Log(1, ex.ToString());
					return;
				}
			}
			else
			{
				_logWriter.Log(1, "WARNING: The websocket was already connected. ");
			}
		}

		private string[] GetAccessionsFromNotification(Notification notification)
		{
			//TODO: check R4 spec - accession is now part of identifiers - combine this and GetMrn methods
			List<string> accessionList = new List<string>();
			foreach(Context context in notification.Event.Contexts)
			{
				if (context.Key == "study")
				{
					accessionList.Add(context.Resource.Accession.Value);
				}
			}
			return accessionList.ToArray();
		}

		private string GetMRNFromNotification(Notification notification)
		{
			string mrn = null;
			foreach (Context context in notification.Event.Contexts)
			{
				if (context.Key == "patient")
				{
					//TODO: search through multiple possible identifiers
					mrn = context.Resource.Identifiers[0].Value;
				}
			}
			return mrn;
		}

		private void _notificationHandler_DoWork(object sender, DoWorkEventArgs e)
		{
			while(true)
			{
				_notificationEvent.WaitOne();
				//_rwEvent.WaitOne(); TODO: create seperate worker thread for PS360 events
				Notification n;
				_notificationQueue.TryDequeue(out n);
				if (null != n)
				{
					// remove other items in the queue. TODO: log them or save them?
					//_notificationQueue.();
					_notificationEvent.Reset();
					_logWriter.Log(1, $"Notification handler got next event in queue: {n.Event.HubEvent}");
					if (n.Event.HubEvent == HubEventType.CloseImagingStudy)
					{
						if (null != _rw.AccessionNumbers)
						{
							// report is opened - close it
							try
							{
								_rw.CancelReport(false);
								_rwEvent.Reset();
							}
							catch (Exception ex)
							{
								_logWriter.Log(1, $"Exception in CloseReport:\r\n:{ex.ToString()}");
								_rwEvent.Set();
							}
						}
					}
					else if (n.Event.HubEvent == HubEventType.OpenImagingStudy || n.Event.HubEvent == HubEventType.SwitchImagingStudy)
					{
						if ( null != _rw.AccessionNumbers )
						{
							// report is open - close it before opening another
							try
							{
								_rw.CancelReport(false);
								_rwEvent.Reset();
								_notificationQueue.Enqueue(n); // put the notification back on the stack 
							}
							catch (Exception ex)
							{
								_logWriter.Log(1, $"Exception in CloseReport:\r\n:{ex.ToString()}");
								_rwEvent.Set();
							}
						}
						else
						{
							try
							{
								string[] accessionNumbers = GetAccessionsFromNotification(n);
								string mrn = GetMRNFromNotification(n);
								_rw.OpenReport(_multiSite, accessionNumbers, mrn);
								_rwEvent.Reset();
							}
							catch (Exception ex)
							{
								_logWriter.Log(1, $"Exception in CloseReport:\r\n:{ex.ToString()}");
								_rwEvent.Set();
							}
						}
					}
					else if (n.Event.HubEvent == HubEventType.UserLogout)
					{
						//TODO: ???
					}
				}
			}
		}

		protected void OnErrorOccurred(string msg)
		{
			if (null != ExceptionOccurred)
				ExceptionOccurred(this, new ExceptionOccurredEventArgs(msg));
		}

		#endregion

		#region IPACS members

		public bool Initialize(string masterArgs, string slaveArgs, int flags, IRadWhere rw)
		{
			//Start logging
			_logWriter.Initialize(_pacsName, PACSFactory.GetLogPath(), 5);
			_logWriter.Log(3, "\r\n------------------------- Initializing " + _pacsName + " PACS integration");
			
			// Only slave mode is supported
			//if ((flags & (int)PACSFlags.Master) > 0)
			//	throw new Exception("The PACS interface is not properly configured. Only slave mode is supported for Imagecast integration.");

			//string[] slaveArgArray = slaveArgs.Split(';');
			//string siteArg = null;
			//for (int i = 0; i < slaveArgArray.Length; i++)
			//{
			//	if (slaveArgArray[i].ToUpper().StartsWith("HUBURL="))
			//	{
			//		_hubUrl = slaveArgArray[i].Substring("HUBURL=".Length).Trim();
			//		_logWriter.Log(3, "HubUrl=" + _hubUrl);
			//	}
			//	else if (slaveArgArray[i].ToUpper().StartsWith("TOPIC="))
			//	{
			//		siteArg = slaveArgArray[i].Substring("TOPIC=".Length).Trim();
			//		if (siteArg.ToUpper() == "USERID")
			//			_topic = TopicType.userid;
			//		else if (siteArg.ToUpper() == "CONFIG")
			//		{
			//			//TODO: implement local config
			//			_topic = TopicType.config;
			//		}
			//		_logWriter.Log(3, "Topic=" + siteArg);
			//	}
			//	else if (slaveArgArray[i].ToUpper().StartsWith("MULTISITE="))
			//	{
			//		siteArg = slaveArgArray[i].Substring("MULTISITE=".Length).Trim();
			//		_logWriter.Log(3, "MultiSite=" + siteArg);
			//		if (siteArg.ToUpper() == "SELECTED")
			//			_multiSite = null;
			//		else if (siteArg.ToUpper() == "THISSITE")
			//			_multiSite = _siteName;
			//		else if (siteArg.ToUpper() != "ALL")
			//			throw new Exception(String.Format("Invalid value \"{0}\" specified for PACS MultiSite argument. Check the Integration Guide for details.", siteArg));
			//	}
			//}

			_rw = rw;
			_rw.ReportClosed += new ReportClosedHandler(_rw_ReportClosed);
			_rw.ReportOpened += new ReportOpenedHandler(_rw_ReportOpened);
			_rw.UserLoggedIn += new UserLoggedInHandler(_rw_UserLoggedIn);
			Subscribe();
			return true;
		}
		public bool Uninitialize(int flags)
		{
			_rw.ReportClosed -= new ReportClosedHandler(_rw_ReportClosed);
			_rw.ReportOpened -= new ReportOpenedHandler(_rw_ReportOpened);
			_rw.UserLoggedIn -= new UserLoggedInHandler(_rw_UserLoggedIn);

			_logWriter.Log(3, _pacsName + " uninitialized.");

			this.Dispose();
			return true;
		}

		public PACSType Type
		{
			get { return PACSType.FHIRCast360; }
		}

		public bool IsInstalled
		{
			get { return true; }
		}

		public bool CanGetImage
		{
			get { return false; }
		}

		public bool CanGetScene
		{
			get	{return false;}
		}

		public bool MustAuthenticate
		{
			get	{ return false; }
		}

		public bool IsStudyOpen
		{
			get	{return null != _rw.AccessionNumbers;}
		}

		public bool Authenticate(string userName, string password)
		{
			return false;
		}

		public bool OpenStudy(PACSContext context)
		{
			return false;
		}

		public Image GetImage(ImageFormat desiredFormat, PACSImageQuality desiredQuality, PACSImageSize desiredSize)
		{
			return null;
		}

		public PACSScene GetScene()
		{
			return null;
		}

		public bool OpenScene(PACSScene scene)
		{
			return false;
		}

		public bool Close(bool hideApplication)
		{
			return false;
		}

		public bool CustomCommand(string command, string args)
		{
			return false;
		}

		#endregion

		#region IDisposable Members

		public void Dispose()
		{
			Close(true);
		}

		#endregion

	}

	#region Model
	#region string constants classes
	public sealed class HubEventType
	{
		public static readonly string OpenImagingStudy = "open-imaging-study";
		public static readonly string SwitchImagingStudy = "switch-imaging-study";
		public static readonly string CloseImagingStudy = "close-imaging-study";
		public static readonly string UserLogout = "user-logout";
	}

	public sealed class ChannelType
	{
		public static readonly string Rest = "rest-hook";
		public static readonly string Websocket = "websocket";
	}

	public sealed class SubscriptionMode
	{
		public static readonly string Subscribe = "subscribe";
		public static readonly string Unsubscribe = "unsubscribe";
	}
	#endregion

	public abstract class ModelBase
	{
		public override string ToString()
		{
			JsonSerializerSettings s = new JsonSerializerSettings();
			s.NullValueHandling = NullValueHandling.Ignore;
			return JsonConvert.SerializeObject(this, s);
		}
	}

	public class Subscription : ModelBase
	{
		public static IEqualityComparer<Subscription> DefaultComparer => new SubscriptionComparer();

		[JsonProperty(PropertyName = "hub.channel")]
		public Channel Channel { get; set; }

		[JsonIgnore]
		public string HubURL { get; set; }

		[JsonProperty(PropertyName = "hub.secret")]
		public string Secret { get; set; }

		[JsonProperty(PropertyName = "hub.callback")]
		public string Callback { get; set; }

		[JsonProperty(PropertyName = "hub.mode")]
		public string Mode { get; set; }

		[JsonProperty(PropertyName = "hub.topic")]
		public string Topic { get; set; }

		[JsonProperty(PropertyName = "hub.events")]
		public string Events { get; set; }
	}

	public sealed class Channel
	{
		//[JsonProperty(PropertyName = "type")]
		public string Type { get; set; }

		//[JsonProperty(PropertyName = "endpoint")]
		public string Endpoint { get; set; }
	}

	public abstract class SubscriptionWithLease : Subscription
	{
		//[JsonProperty(PropertyName = "hub.lease_seconds")]
		public int? LeaseSeconds { get; set; }

		//[JsonIgnore]
		public TimeSpan? Lease => this.LeaseSeconds.HasValue ? TimeSpan.FromSeconds(this.LeaseSeconds.Value) : (TimeSpan?)null;
	}

	public sealed class SubscriptionVerification : SubscriptionWithLease
	{
		//[JsonProperty(PropertyName = "hub.challenge")]
		public string Challenge { get; set; }
	}

	public sealed class SubscriptionCancelled : Subscription
	{
		//[JsonProperty(PropertyName = "hub.reason")]
		public string Reason { get; set; }
	}

	public sealed class Notification : ModelBase
	{
		[JsonProperty(PropertyName = "timestamp")]
		public DateTime Timestamp { get; set; }

		[JsonProperty(PropertyName = "id")]
		public string Id { get; set; }

		[JsonProperty(PropertyName = "event")]
		public NotificationEvent Event { get; set; }

		[JsonProperty(PropertyName = "status")]
		public string Status { get; set; }

		[JsonProperty(PropertyName = "statuscode")]
		public int StatusCode { get; set; }
	}
	public sealed class WebSocketResponse : ModelBase
	{
		[JsonProperty(PropertyName = "timestamp")]
		public DateTime Timestamp { get; set; }

		[JsonProperty(PropertyName = "status")]
		public string Status { get; set; }

		[JsonProperty(PropertyName = "statuscode")]
		public int StatusCode { get; set; }
	}
	public class WebSocketConnection : ModelBase
	{
		public WebSocketConnection(DateTime timeStamp, string topic, System.Net.WebSockets.WebSocket ws)
		{
			this.TimeStamp = timeStamp;
			this.Topic = topic;
			this.WebSocket = ws;
		}
		public DateTime TimeStamp;
		public string Topic;
		public System.Net.WebSockets.WebSocket WebSocket;
	}

	public sealed class NotificationEvent : ModelBase
	{
		[JsonProperty(PropertyName = "hub.topic")]
		public string Topic { get; set; }

		[JsonProperty(PropertyName = "hub.event")]
		public string HubEvent { get; set; }

		[JsonProperty(PropertyName = "context")]
		public Context[] Contexts { get; set; }
	}

	public sealed class Context : ModelBase
	{
		[JsonProperty(PropertyName = "key")]
		public string Key { get; set; }

		[JsonProperty(PropertyName = "resource")]
		public Resource Resource { get; set; }
	}

	// Can be a Patient Resource or ImagingStudy resource
	// TODO: create subclasses?
	public sealed class Resource : ModelBase
	{
		[JsonProperty(PropertyName = "resourceType")]
		public string ResourceType { get; set; }

		[JsonProperty(PropertyName = "id")]
		public string Id { get; set; }

		[JsonProperty(PropertyName = "identifier")]
		public Identifier[] Identifiers { get; set; }

		[JsonProperty(PropertyName = "uid")]
		public string Uid { get; set; }

		[JsonProperty(PropertyName = "patient")]
		public ResourceReference Patient { get; set; }

		[JsonProperty(PropertyName = "accession")]
		public Identifier Accession { get; set; }
	}

	public class Identifier : ModelBase
	{
		[JsonProperty(PropertyName = "use")]
		public string Use { get; set; }

		[JsonProperty(PropertyName = "type")]
		public Coding Type { get; set; }

		[JsonProperty(PropertyName = "system")]
		public string System { get; set; }

		[JsonProperty(PropertyName = "value")]
		public string Value { get; set; }
	}

	public sealed class CodeableConcept : ModelBase
	{
		[JsonProperty(PropertyName = "coding")]
		public Coding Coding;

		[JsonProperty(PropertyName = "text")]
		public Coding Text;
	}

	public sealed class Coding : ModelBase
	{
		[JsonProperty(PropertyName = "system")]
		public string system { get; set; }

		[JsonProperty(PropertyName = "version")]
		public string version { get; set; }

		[JsonProperty(PropertyName = "code")]
		public string Code { get; set; }

		[JsonProperty(PropertyName = "display")]
		public string Display { get; set; }

		[JsonProperty(PropertyName = "userSelected")]
		public bool UserSelected { get; set; }
	}

	public sealed class ResourceReference : ModelBase
	{
		[JsonProperty(PropertyName = "reference")]
		public string Reference { get; set; }
	}

	public class SubscriptionComparer : IEqualityComparer<Subscription>
	{
		public bool Equals(Subscription sub1, Subscription sub2)
		{
			return sub1.Topic == sub2.Topic;
		}

		public int GetHashCode(Subscription subscription)
		{
			return subscription.Topic.GetHashCode();
		}
	}
	#endregion
}