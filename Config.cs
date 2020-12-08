using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Linq;

namespace HomeAutomation.RgbLight
{
	/// <summary>
	/// Все настройки приложения
	/// </summary>
	public class Config
	{
		/// <summary>
		/// Версия приложения
		/// </summary>
		public string AppVersion { get; set; } = Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
		/// <summary>
		/// Настройки подключения к MQTT 
		/// </summary>
		public class MqttType
		{
			/// <summary>
			/// Хост MQTT-сервера
			/// </summary>
			public string Host { get; set; } = "localhost";
			public int? Port { get; set; }
			/// <summary>
			/// Имя пользователя если есть
			/// </summary>
			public string Username { get; set; }
			/// <summary>
			/// Пароль
			/// </summary>
			public string Password { get; set; }
		}
		public MqttType Mqtt { get; set; } = new MqttType();
		/// <summary>
		/// Топики с событиями изменения разных значений (устанавливать можно через подтопик /set)
		/// </summary>
		public class TopicsType
		{
			/// <summary>
			/// Цвет лампочки
			/// </summary>
			public string Color { get; set; } = "zw/bulb/51/1/0";
			/// <summary>
			/// Датчик движения
			/// </summary>
			public string Motion { get; set; } = "zw/motion/48/1/0";
			/// <summary>
			/// Открытие/закрытие двери
			/// </summary>
			public string Door { get; set; } = "zw/door/48/1/0";

		}
		public TopicsType Topics { get; set; } = new TopicsType();
		/// <summary>
		/// Настройки анимации по датчику движения
		/// </summary>
		public class MotionAnimationType
		{
			/// <summary>
			/// Длительность всей анимации в миллисекундах
			/// </summary>
			public int TotalAnimationLength { get; set; } = 14000;
			/// <summary>
			/// Сколько времени показывается каждый цвет
			/// </summary>
			public int SingleColorLength { get; set; } = 2500;
		}
		public MotionAnimationType MotionAnimation { get; set; } = new MotionAnimationType();
		/// <summary>
		/// Настройки анимации по событию двери
		/// </summary>
		public class DoorAnimationType
		{
			/// <summary>
			/// Цвет, когда открыли дверь
			/// </summary>
			public string OpenedColor { get; set; } = "#00FF000000";
			/// <summary>
			/// Цвет, когда закрыли дверь
			/// </summary>
			public string ClosedColor { get; set; } = "#FF00000000";
			/// <summary>
			/// Сколько времени показывать цвет когда дверь открыли, в мс
			/// </summary>
			public int OpenedColorDuration { get; set; } = 10000;
		}
		public DoorAnimationType DoorAnimation { get; set; } = new DoorAnimationType();
	}
}
