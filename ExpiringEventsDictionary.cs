using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;

namespace HomeAutomation.RgbLight
{
	/// <summary>
	/// Словарь "ключ-событие", где записи могут протухать. Время жизни задаётся в конструкторе, 
	/// удаление старых записей происходит во время добавления новых. Событие срабатывает, если мы удаляем ключ
	/// из словаря методом FireEventIfExists.
	/// </summary>
	public class ExpiringEventsDictionary
	{
		/// <summary>
		/// Время жизни объекта
		/// </summary>
		private readonly TimeSpan ttl;
		private readonly Dictionary<string, DateTime> keys = new Dictionary<string, DateTime>();
		private readonly Dictionary<string, ManualResetEvent> events = new Dictionary<string, ManualResetEvent>();
		public ExpiringEventsDictionary(TimeSpan lifetime)
		{
			this.ttl = lifetime;
		}
		/// <summary>
		/// Добавить ключ. Потокобезопасный, удаляет истёкшие ключи
		/// </summary>
		/// <param name="key"></param>
		/// <returns>Событие, которое выстрелит при удалении записи</returns>
		public ManualResetEvent Add(string key)
		{
			lock(keys)
			{
				var e = new ManualResetEvent(false);
				keys[key] = DateTime.Now + ttl;
				events[key] = e;
				var expired = keys.Where(a => a.Value < DateTime.Now).Select(a => a.Key);
				foreach (var k in expired)
					RemoveKey(k);
				return e;
			}
		}

		private ManualResetEvent RemoveKey(string key)
		{
			keys.Remove(key);
			var e = events[key];
			events.Remove(key);
			return e;
		}
		/// <summary>
		/// 
		/// </summary>
		/// <param name="key"></param>
		public bool FireEventIfExists(string key)
		{
			lock (keys)
			{
				// Мы помним про такой ключ
				if (keys.ContainsKey(key))
				{
					var e = RemoveKey(key);
					e.Set();
					return true;
				}
				return false;
			}
		}
	}
}
