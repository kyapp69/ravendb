﻿using System;
using Raven.Server.Documents;
using Voron.Impl;

namespace Raven.Server.ServerWide
{
    public class RavenTransaction : IDisposable
    {
        public readonly Transaction InnerTransaction;

        public RavenTransaction(Transaction transaction)
        {
            InnerTransaction = transaction;
        }

        public void Commit()
        {
            InnerTransaction.Commit();
        }

        public void EndAsyncCommit()
        {
            InnerTransaction.EndAsyncCommit();
        }

        public bool Disposed;
        public virtual void Dispose()
        {
            if (Disposed)
                return;

            Disposed = true;

            InnerTransaction?.Dispose();
        }
    }
}