using System;
using System.Diagnostics;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Client;

namespace Fluidity.Raven.Lock
{
	public sealed class Locker : ILocker
	{
		private const int TickMilliseconds = 50;

		private readonly IDocumentStore _documentStore;
		private readonly string _lockName;
		private Lock _lock;
		private Etag _lockEtag;
		private IDocumentSession _session;

		/// <summary>
		///     Initializes a new instance of the <see cref="Locker" /> class.
		/// </summary>
		/// <param name="documentStore">The documentStore.</param>
		/// <param name="lockName">Name of the lock.</param>
		/// <param name="timeout">The timeout.</param>
		/// <param name="lifetime">The lifetime.</param>
		public Locker(IDocumentStore documentStore, string lockName, TimeSpan timeout, TimeSpan lifetime)
		{
			_documentStore = documentStore;
			_lockName = lockName;
			_session = CreateSession();

			WaitToLock(timeout, lifetime);
		}

		/// <summary>
		///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
		/// </summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		///     Finalizes an instance of the <see cref="Locker" /> class.
		/// </summary>
		~Locker()
		{
			Dispose(false);
		}

		/// <summary>
		///     Releases the mutex.
		/// </summary>
		private void Dispose(bool disposing)
		{
			if (_session != null && _lock != null)
			{
				try
				{
					_session.Delete(_lock);
					_session.SaveChanges();
				}
				finally
				{
					_lock = null;
				}
			}

			if (_session != null)
				_session.Dispose();

			if (disposing)
				_session = null;
		}

		/// <summary>
		///     Cria uma nova sess�o de documentos.
		/// </summary>
		/// <returns></returns>
		private IDocumentSession CreateSession()
		{
			IDocumentSession session = _documentStore.OpenSession();

			session.Advanced.UseOptimisticConcurrency = true;
			session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;

			return session;
		}

		/// <summary>
		///     Tenta adquirir o lock, esperando dentro do tempo limite especificado.
		/// </summary>
		/// <param name="timeout">The timeout.</param>
		/// <param name="lifetime">The lifetime.</param>
		/// <returns></returns>
		/// <exception cref="System.TimeoutException"></exception>
		private void WaitToLock(TimeSpan timeout, TimeSpan lifetime)
		{
			Stopwatch stopWatch = new Stopwatch();
			stopWatch.Start();

			int attempt = 0;

			do
			{
				bool expired = stopWatch.Elapsed > timeout;

				if (expired)
					throw new TimeoutException();

				if (TryAcquireLock(_lockName, lifetime, ++attempt))
					break;

				Wait();

			} while (true);
		}

		/// <summary>
		/// Tenta obter um lock (criando um objeto Lock no servidor remoto).
		/// </summary>
		/// <param name="lockName">O nome do lock</param>
		/// <param name="lifetime">The lifetime.</param>
		/// <param name="attempt">The attempt.</param>
		/// <returns>
		///   <c>true</c> se foi poss�vel adquirir o lock; caso contr�rio, <c>false</c>.
		/// </returns>
		private bool TryAcquireLock(string lockName, TimeSpan lifetime, int attempt)
		{
			bool acquired = false;

			_lock = new Lock {
				Id = string.Format("Locks/{0}", lockName),
				Expiration = DateTime.UtcNow.Add(lifetime)
			};

			try
			{
				_session.Store(_lock, Etag.Empty, _lock.Id);
				_session.SaveChanges();
				_lockEtag = _session.Advanced.GetEtagFor(_lock);
				acquired = true;
			}
			catch (ConcurrencyException)
			{
				Trace.WriteLine("Session already locked for " + lockName);

				if (attempt % 3 == 0)
					RemoveExpiredLock(_lock.Id);

				_lock = null;
				_session.Advanced.Clear();
			}

			return acquired;
		}

		/// <summary>
		///     Estende o tempo de vido do lock, para garantir que a tarefa seja executada.
		/// </summary>
		/// <param name="lifetime">The lifetime.</param>
		/// <exception cref="System.NotImplementedException"></exception>
		public void Renew(TimeSpan lifetime)
		{
			_lock.Expiration = DateTime.UtcNow + lifetime;
			_session.Store(_lock, _lockEtag, _lock.Id);
			_session.SaveChanges();
			_lockEtag = _session.Advanced.GetEtagFor(_lock);
		}

		/// <summary>
		///     Remove um lock criado anteriormente que j� expirou.
		/// </summary>
		/// <param name="lockId"></param>
		private void RemoveExpiredLock(string lockId)
		{
			using (IDocumentSession session = _documentStore.OpenSession())
			{
				var lockInstance = session.Load<Lock>(lockId);

				if (lockInstance != null && lockInstance.Expired)
				{
					session.Delete(lockInstance);
					session.SaveChanges();
				}
			}
		}

		/// <summary>
		///     Espera por um tempo especificado para tentar novamente.
		/// </summary>
		private void Wait()
		{
			Thread.Sleep(TickMilliseconds);
		}
	}
}