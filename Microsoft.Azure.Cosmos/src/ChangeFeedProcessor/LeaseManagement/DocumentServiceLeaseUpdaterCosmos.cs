﻿//----------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//----------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.ChangeFeed.LeaseManagement
{
    using System;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.ChangeFeed.Exceptions;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// <see cref="DocumentServiceLeaseUpdater"/> that uses Azure Cosmos DB
    /// </summary>
    internal sealed class DocumentServiceLeaseUpdaterCosmos : DocumentServiceLeaseUpdater
    {
        private const int RetryCountOnConflict = 5;
        private readonly CosmosContainer container;

        public DocumentServiceLeaseUpdaterCosmos(CosmosContainer container)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            this.container = container;
        }

        public override async Task<DocumentServiceLease> UpdateLeaseAsync(DocumentServiceLease cachedLease, string itemId, object partitionKey, Func<DocumentServiceLease, DocumentServiceLease> updateLease)
        {
            DocumentServiceLease lease = cachedLease;
            for (int retryCount = RetryCountOnConflict; retryCount >= 0; retryCount--)
            {
                lease = updateLease(lease);
                if (lease == null)
                {
                    return null;
                }

                lease.Timestamp = DateTime.UtcNow;
                DocumentServiceLeaseCore leaseDocument = await this.TryReplaceLeaseAsync((DocumentServiceLeaseCore)lease, partitionKey, itemId).ConfigureAwait(false);
                if (leaseDocument != null)
                {
                    return leaseDocument;
                }

                DefaultTrace.TraceInformation("Lease with token {0} update conflict. Reading the current version of lease.", lease.CurrentLeaseToken);

                ItemResponse<DocumentServiceLeaseCore> response = await this.container.ReadItemAsync<DocumentServiceLeaseCore>(
                    partitionKey, itemId).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    DefaultTrace.TraceInformation("Lease with token {0} no longer exists", lease.CurrentLeaseToken);
                    throw new LeaseLostException(lease, true);
                }

                DocumentServiceLeaseCore serverLease = response.Resource;

                DefaultTrace.TraceInformation(
                    "Lease with token {0} update failed because the lease with concurrency token '{1}' was updated by host '{2}' with concurrency token '{3}'. Will retry, {4} retry(s) left.",
                    lease.CurrentLeaseToken,
                    lease.ConcurrencyToken,
                    serverLease.Owner,
                    serverLease.ConcurrencyToken,
                    retryCount);

                lease = serverLease;
            }

            throw new LeaseLostException(lease);
        }

        private async Task<DocumentServiceLeaseCore> TryReplaceLeaseAsync(DocumentServiceLeaseCore lease, object partitionKey, string itemId)
        {
            try
            {
                ItemResponse<DocumentServiceLeaseCore> response = await this.container.ReplaceItemAsync<DocumentServiceLeaseCore>(
                    lease, 
                    this.CreateIfMatchOptions(lease)).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new LeaseLostException(lease, true);
                }

                return response.Resource;
            }
            catch (CosmosException ex)
            {
                DefaultTrace.TraceWarning("Lease operation exception, status code: {0}", ex.StatusCode);
                if (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    return null;
                }

                if (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    throw new LeaseLostException(lease, ex, false);
                }

                throw;
            }
        }

        private ItemRequestOptions CreateIfMatchOptions(DocumentServiceLease lease)
        {
            return new ItemRequestOptions { IfMatchEtag = lease.ConcurrencyToken };
        }
    }
}
