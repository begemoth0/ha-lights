using System;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.IO;

namespace HomeAutomation.RgbLight
{
	class Program
	{
		/// <summary>
		/// Логгер для события анимации
		/// </summary>
		private static readonly NLog.Logger animationLog = NLog.LogManager.GetLogger("Animation");
		/// <summary>
		/// Логгер для событий MQTT-клиента
		/// </summary>
		private static readonly NLog.Logger mqttLog = NLog.LogManager.GetLogger("Mqtt");
		/// <summary>
		/// Логгер для событий бизнес-логики
		/// </summary>
		private static readonly NLog.Logger logicLog = NLog.LogManager.GetLogger("Logic");
		/// <summary>
		/// Дефолтный логгер
		/// </summary>
		private static readonly NLog.Logger log = NLog.LogManager.GetCurrentClassLogger();

		static void PrintUsageAndExit(int code, string errorMsg = null)
		{
			Console.WriteLine($"Home automation script for 1 RGB led light and two sensors connected via Z-Wave over MQTT. Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()}");
			Console.WriteLine($"Usage: {Path.GetFileName(System.Reflection.Assembly.GetEntryAssembly().Location)} [export-config [filename] | [config-to-use]");
			if (!string.IsNullOrEmpty(errorMsg))
			{
				Console.WriteLine();
				Console.WriteLine(errorMsg);
			}
			Environment.Exit(code);
		}

		private static ManualResetEvent TerminationEvent = new ManualResetEvent(false);
		static void Main(string[] args)
		{
			// настроим логгер
			var config = new NLog.Config.LoggingConfiguration();
			// Targets where to log to: File and Console
			var logconsole = new NLog.Targets.ConsoleTarget("logconsole");
			// Rules for mapping loggers to targets            
			config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, logconsole);
			// Apply config           
			NLog.LogManager.Configuration = config;

			// прочитаем файл с настройками
			string defaultConfig = "./rgbLights.conf.json";
			var cfg = new Config();
			if (File.Exists(defaultConfig))
			{
				cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(defaultConfig));
			}
			// прочитаем параметры из командной строки
			if (args.Length > 0)
			{
				if (args[0] == "export-config")
				{
					var target = args.Length > 1 ? args[1] : defaultConfig;
					File.WriteAllText(target, JsonConvert.SerializeObject(cfg));
					Console.WriteLine($"Config successfully written to {target}");
					Environment.Exit(0);
				}
				else
				{
					if (!File.Exists(args[0]))
						PrintUsageAndExit(-1, $"File not found: {Path.GetFullPath(args[0])}");
					cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(args[0]));
				}
			}

			var factory = new MqttFactory();
			var mqttClient = factory.CreateMqttClient();
			var optionsBuilder = new MqttClientOptionsBuilder()
				.WithClientId($"{System.Reflection.Assembly.GetEntryAssembly().GetName().Name}-v.{cfg.AppVersion}")
				.WithTcpServer(cfg.Mqtt.Host)
				.WithCleanSession();
			if (!string.IsNullOrEmpty(cfg.Mqtt.Username))
				optionsBuilder = optionsBuilder.WithCredentials(cfg.Mqtt.Username, cfg.Mqtt.Password);
			var options = optionsBuilder.Build();
			// Subscribe to termination events
			Console.CancelKeyPress += (s, e) =>
			{
				log.Info($"{e.SpecialKey} pressed. Terminating...");
				e.Cancel = true;
				TerminationEvent.Set();
			};
			AppDomain.CurrentDomain.ProcessExit += (s, e) =>
			{
				log.Info($"Exit signal caught: {e}. Terminating...");
				TerminationEvent.Set();
			};
			mqttClient.UseDisconnectedHandler(e =>
			{
				log.Error($"Connection to server failed: {e.ReasonCode}");
				Environment.Exit(1);
			});
			var connectResult = mqttClient.ConnectAsync(options).Result;
			log.Info($"Connected to MQTT server: {cfg.Mqtt.Host} Client ID: {options.ClientId}.");
			// TODO: get topics from command line
			var topicHandlers = new Dictionary<string, Action<JToken>>
			{
				{ cfg.Topics.Color, jsonVal => HandleColorChangedEvent(jsonVal.Value<string>()) },
				{ cfg.Topics.Motion, jsonVal => HandleMotion(jsonVal.Value<bool>(), mqttClient, cfg) },
				{ cfg.Topics.Door, jsonVal => HandleDoor(jsonVal.Value<bool>(), mqttClient, cfg) }
			};
			var topicValues = new Dictionary<string, string>();
			mqttClient.UseApplicationMessageReceivedHandler(e =>
			{
				var jsonMsg = JObject.Parse(e.ApplicationMessage.ConvertPayloadToString());
				var jsonVal = jsonMsg["value"];
				var newVal = jsonVal.ToString();
				string topic = e.ApplicationMessage.Topic;
				var oldVal = topicValues.ContainsKey(topic) ? topicValues[topic] : "";
				mqttLog.Debug($"< Message received: {topic}, {oldVal} -> {newVal}");
				// Если значение изменилось -- вызовем обработчик
				if (oldVal != newVal)
				{
					topicHandlers[topic](jsonVal);
					topicValues[topic] = newVal;
				}
			});

			// подписываемся на все топики со значениями
			foreach (var t in topicHandlers.Keys)
			{
				if (!string.IsNullOrEmpty(t))
				{
					var sr = mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build()).Result.Items[0];
					log.Debug($"Subscribed: {sr.TopicFilter} - {sr.ResultCode}");
				}
			}
			// Main loop
			TerminationEvent.WaitOne();
		}
		/// <summary>
		/// Цвет, на который мы восстанавливаемся в idle
		/// </summary>
		private static string idleColor = "#0000000000";
		/// <summary>
		/// Куда мы сохраняем idleColor, если решили его временно изменить
		/// </summary>
		private static string stashedIdleColor = null;
		/// <summary>
		/// Установки какого цвета мы ждём
		/// </summary>
		private static readonly ExpiringEventsDictionary outstandingColorRequests = new ExpiringEventsDictionary(TimeSpan.FromSeconds(30));
		private static void HandleColorChangedEvent(string value)
		{
			// если мы запрашивали этот цвет -- игнорируем. Если не мы, то считаем его новым дефолтом
			if (!outstandingColorRequests.FireEventIfExists(value))
			{
				if (value != idleColor)
				{
					idleColor = value;
					stashedIdleColor = null;
					StopAnimation();
					logicLog.Debug($"Setting new default color {value}");
				}
			}
		}

		/// <summary>
		/// Отправить сообщение на изменение цвета
		/// </summary>
		/// <param name="client"></param>
		/// <param name="colorTopic"></param>
		/// <param name="targetColor"></param>
		/// <returns></returns>
		private static Task<MQTTnet.Client.Publishing.MqttClientPublishResult> PublishSetColorMessage(IMqttClient client, string colorTopic, string targetColor)
		{
			var msg = new MqttApplicationMessageBuilder()
				.WithTopic(colorTopic + "/set")
				.WithPayload(targetColor)
				.WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
				.Build();
			mqttLog.Debug($"> Sending message: {msg.Topic}, {msg.ConvertPayloadToString()}");
			return client.PublishAsync(msg);
		}
		/// <summary>
		/// Синхронно изменить цвет
		/// </summary>
		/// <param name="cancel">Токен отмены ожидания</param>
		/// <param name="timeout">Сколько времени ждать ответа</param>
		/// <returns>True -- мы получили подтверждение установки запрошенного цвета. False -- таймаут.</returns>
		private static bool SetColorSync(CancellationToken cancel, TimeSpan timeout, IMqttClient client, string colorTopic, string targetColor)
		{
			var waitForColor = outstandingColorRequests.Add(targetColor);
			var r = PublishSetColorMessage(client, colorTopic, targetColor);
			var watch = System.Diagnostics.Stopwatch.StartNew();
			if (waitForColor.WaitOne(timeout))
			{
				watch.Stop();
				animationLog.Debug($"Color {targetColor} ACK received. RTT: {watch.ElapsedMilliseconds} ms");
				return true;
			}
			else
			{
				animationLog.Error($"Color change to {targetColor} timed out waiting for ACK");
				return false;
			}
		}
		private static readonly object currentAnimationLock = new object();
		private static CancellationTokenSource stopAnimationCts;
		private static Task currentAnimation;
		private static string currentAnimationDesc;
		/// <summary>
		/// Остановить текущую анимацию -- интерфейсный метод, для вызовов в логике
		/// </summary>
		/// <returns></returns>
		private static void StopAnimation()
		{
			lock (currentAnimationLock)
			{
				CancelTaskAndWaitRaw();
				currentAnimation = null;
				stopAnimationCts = null;
				currentAnimationDesc = null;
			}
		}
		private static void StartNewAnimation(Action<CancellationToken> cancellableAction, string description)
		{
			lock (currentAnimationLock)
			{
				CancelTaskAndWaitRaw();
				stopAnimationCts = new CancellationTokenSource();
				currentAnimation = Task.Run(() => cancellableAction(stopAnimationCts.Token));
				currentAnimationDesc = description;
			}
		}
		/// <summary>
		/// Остановить анимацию и дождаться завершения -- внутренний метод, для вызова из критических секций
		/// </summary>
		private static void CancelTaskAndWaitRaw()
		{
			if (currentAnimation == null || currentAnimation.IsCompleted)
				return;
			log.Debug($"Stopping {currentAnimationDesc}...");
			stopAnimationCts.Cancel();
			currentAnimation.Wait();
		}

		// установить цвет на указанное количество мс
		private static bool SetColorForDuration(CancellationToken cancel, IMqttClient client, string colorTopic, string color, TimeSpan duration)
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			if (!SetColorSync(cancel, TimeSpan.FromSeconds(3), client, colorTopic, color))
				return false;
			watch.Stop();
			// дожидаем сколько осталось
			if (watch.ElapsedMilliseconds < duration.TotalMilliseconds)
			{
				if (cancel.WaitHandle.WaitOne((int)(duration.TotalMilliseconds - watch.ElapsedMilliseconds)))
					return false;
			}
			return true;
		}
		private static int permutationIndex = -1;
		private static void HandleMotion(bool state, IMqttClient client, Config cfg)
		{
			// нас не волнует событие "переход в ЛОЖ"
			if (!state)
				return;
			StartNewAnimation((token) =>
			{
				Random r = new Random();
				// мы генерируем цвета по HSV. В этом случае для генерации максимально насыщенного цвета нам 
				// надо зафиксировать одну компоненту на максимальной яркости, а вторую взять рандомную.
				// третья компонента не нужна потому что просто разбавит нам цвет.
				var cv = new List<int>
				{
					r.Next(20, 255),
					255,
				};
				cv.Sort();
				// перебираем наборы используемых координат по очереди, чтобы не повторяться
				var permutations = new[,]
				{
					{ 0, 1, 2 },
					{ 1, 2, 0 },
					{ 2, 0, 1 },
					{ 2, 1, 0 },
					{ 1, 0, 2 },
					{ 0, 2, 1 }
				};
				permutationIndex = (permutationIndex + 1) % permutations.GetLength(0);
				var cycleColors = new List<string>();
				var mixedColorBytes = new byte[5];
				for (int i = 0; i < cv.Count(); i++)
				{
					var componentIndex = permutations[permutationIndex, i];
					var componentValue = (byte)cv[i];
					// создадим чистый однокомпонентный цвет
					var cleanColor = new byte[5];
					cleanColor[componentIndex] = componentValue;
					cycleColors.Add(ColorFromBytes(cleanColor));
					mixedColorBytes[componentIndex] = componentValue;
				}
				var mixedColor = ColorFromBytes(mixedColorBytes);
				// с перебивкой в виде чёрного смесь смотрится приятнее 
				cycleColors.Add("#0000000000");
				bool cycleThroughColors(List<string> cycleColors, string resultColor)
				{
					// обёртка с основными локальными параметрами
					bool waitWithColor(string color, int duration)
					{
						return SetColorForDuration(token, client, cfg.Topics.Color, color, TimeSpan.FromMilliseconds(duration));
					};
					var totalRunning = System.Diagnostics.Stopwatch.StartNew();
					foreach (var color in cycleColors)
					{
						if (!waitWithColor(color, cfg.MotionAnimation.SingleColorLength))
							return false;
					}
					// сколько времени нам пыриться на итоговый цвет (до следующего возможного срабатывания датчика)
					var durationLeft = cfg.MotionAnimation.TotalAnimationLength - (int)totalRunning.ElapsedMilliseconds;
					// простое охранное условие
					if (durationLeft < 0)
						durationLeft = 3000;
					// Дадим полюбоваться последним цветом
					if (!waitWithColor(mixedColor, durationLeft))
						return false;
					return true;
				}
				animationLog.Info($"Starting motion: {string.Join("->", cycleColors.ToArray())}->{mixedColor}->{idleColor}");
				// Избегаем коллизии с остатком данных от датчика движения
				Thread.Sleep(600);
				var success = cycleThroughColors(cycleColors, mixedColor);
				// Возвращаем цвет по умолчанию, если только нас не отменили
				if (!token.IsCancellationRequested)
				{
					SetColorSync(token, TimeSpan.FromSeconds(3), client, cfg.Topics.Color, idleColor);
					animationLog.Debug($"Motion {(success ? "finished" : "failed")}.");
				}
			}, "motion animation");
		}
		private static string ColorFromBytes(byte[] color)
		{
			return $"#{color[0]:x2}{color[1]:x2}{color[2]:x2}{color[3]:x2}{color[4]:x2}".ToUpper();
		}

		private static void HandleDoor(bool state, IMqttClient client, Config cfg)
		{
			string closedDoorColor = ColorFromBytes(new byte[] { 255, 0, 0, 0, 0 });
			string openDoorColor = ColorFromBytes(new byte[] { 0, 255, 0, 0, 0 });

			// дверь закрыли
			if (!state)
			{
				animationLog.Info($"Door closed. Stashing idle color {idleColor}, setting {closedDoorColor}.");
				StopAnimation();
				stashedIdleColor = idleColor;
				idleColor = closedDoorColor;
				PublishSetColorMessage(client, cfg.Topics.Color, idleColor);
			}
			else
			{
				// если дефолтный цвет не сбрасывали -- возвращаем его
				var restoredDesc = "idle";
				if (stashedIdleColor != null)
				{
					idleColor = stashedIdleColor;
					restoredDesc = "stashed";
				}
				StartNewAnimation((token) =>
				{
					animationLog.Info($"Door opened. Playing {openDoorColor}->{idleColor} color [{restoredDesc}].");
					var success = SetColorForDuration(token, client, cfg.Topics.Color, openDoorColor, TimeSpan.FromSeconds(10));
					if (!token.IsCancellationRequested)
					{
						SetColorSync(token, TimeSpan.FromSeconds(3), client, cfg.Topics.Color, idleColor);
						animationLog.Debug($"Door open {(success ? "finished" : "failed")}.");
					}
				}, "door open animation");
			}
		}
	}
}
