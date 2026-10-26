// Compiled only while BOTH Unity IAP and CAS are in the project — the defineConstraints on this
// assembly (UNITY_PURCHASING / CAS_SDK_PRESENT, each a version define on the package that
// provides it). Without Unity IAP the method simply does not exist in the build, and a game on
// another payment system keeps reporting through CAS.PurchaseInfo by hand, exactly as before.
using System;
using CAS;
using UnityEngine;
using UnityEngine.Purchasing;

namespace CAS.Publishing.Installer.Runtime
{
    /// <summary>
    /// Runtime entry points of the Hub. Today: purchase reporting for games on Unity IAP.
    ///
    /// CAS forwards every reported purchase to the attribution SDK it hosts (Tenjin MMP), so this
    /// is the one call a game makes per purchase — nothing has to be reported to Tenjin separately.
    /// The game keeps the whole receipt-parsing dance
    /// (<c>UnifiedReceipt</c> → store → <c>SetGoogleValidation</c> / <c>SetAppleValidation</c> /
    /// <c>SetAmazonValidation</c> → <c>MobileAds.ReportPurchase</c>) out of its own code:
    /// <code>
    /// // Unity IAP 5 — inside the purchase-pending callback:
    /// CASHub.ReportPurchase(product, pendingOrder);
    ///
    /// // Legacy IStoreListener / Unity IAP 4:
    /// CASHub.ReportPurchase(args.purchasedProduct);
    /// </code>
    /// Report before confirming the order: after confirmation a consumable's receipt is gone.
    /// </summary>
    public static class CASHub
    {
        private const string LogPrefix = "[CAS Hub]";

        // Store names as Unity IAP writes them into UnifiedReceipt.Store.
        private const string StoreGooglePlay    = "GooglePlay";
        private const string StoreAppleAppStore = "AppleAppStore";
        private const string StoreMacAppStore   = "MacAppStore";
        private const string StoreAmazon        = "AmazonAppStore";
        private const string StoreFake          = "fake";   // the Editor's FakeStore

#if UNITY_PURCHASING_5
        /// <summary>
        /// Reports one purchased product of an order to CAS (and, through CAS, to attribution).
        /// Takes the Unity IAP 5 objects as they arrive in the purchase-pending callback: the
        /// <see cref="Product"/> bought and the <see cref="Order"/> that carries its receipt. Google
        /// Play, the Apple App Store and the Amazon Appstore get their validation data attached; any
        /// other store is reported without validation and logged.
        /// </summary>
        public static void ReportPurchase(Product product, Order order)
        {
            if (product == null || order == null || order.Info == null)
            {
                Debug.LogWarning($"{LogPrefix} ReportPurchase skipped: product or order is null.");
                return;
            }

            var info = order.Info;
            var apple = info.Apple;
            Report(
                product,
                QuantityOf(product, order),
                info.Receipt,
                info.TransactionID,
                apple?.jwsRepresentation,
                apple?.AppReceipt);
        }

        /// <summary>
        /// Reports every product in the order's cart — for callbacks that hand over only the
        /// <see cref="Order"/>. One CAS report per cart line, quantity included.
        /// </summary>
        public static void ReportPurchase(Order order)
        {
            if (order == null || order.CartOrdered == null)
            {
                Debug.LogWarning($"{LogPrefix} ReportPurchase skipped: order or its cart is null.");
                return;
            }

            var items = order.CartOrdered.Items();
            if (items == null || items.Count == 0)
            {
                Debug.LogWarning($"{LogPrefix} ReportPurchase skipped: the order's cart is empty.");
                return;
            }

            foreach (var item in items)
                ReportPurchase(item.Product, order);
        }

        private static int QuantityOf(Product product, Order order)
        {
            var items = order.CartOrdered?.Items();
            if (items == null) return 1;

            var id = product.definition?.id;
            foreach (var item in items)
            {
                if (item.Product == null || item.Product.definition?.id != id) continue;
                return item.Quantity > 0 ? item.Quantity : 1;
            }
            return 1;
        }
#endif

        /// <summary>
        /// Reports a purchased product using the receipt the product itself carries
        /// (<c>product.receipt</c> / <c>product.transactionID</c>) — the shape of the legacy
        /// <c>IStoreListener.ProcessPurchase</c> callback and of Unity IAP 4. On Apple the legacy
        /// receipt has no JWS transaction; the binary app receipt from the payload is sent instead.
        /// </summary>
        public static void ReportPurchase(Product product)
        {
            if (product == null)
            {
                Debug.LogWarning($"{LogPrefix} ReportPurchase skipped: product is null.");
                return;
            }

            Report(product, 1, product.receipt, product.transactionID, null, null);
        }

        private static void Report(
            Product product, int quantity, string receiptJson, string transactionId,
            string appleJws, string appleReceipt)
        {
            var purchase = Build(product, quantity, receiptJson, transactionId, appleJws, appleReceipt);
            if (purchase == null) return;
            MobileAds.ReportPurchase(purchase);
        }

        /// <summary>
        /// Builds the <see cref="PurchaseInfo"/> CAS expects; null only when the product carries no
        /// definition or metadata (nothing to price). Kept separate from the CAS call so the
        /// receipt → validation mapping is testable.
        /// </summary>
        internal static PurchaseInfo Build(
            Product product, int quantity, string receiptJson, string transactionId,
            string appleJws, string appleReceipt)
        {
            var definition = product.definition;
            var metadata = product.metadata;
            if (definition == null || metadata == null)
            {
                Debug.LogWarning($"{LogPrefix} ReportPurchase skipped: product has no definition or metadata.");
                return null;
            }

            var purchase = new PurchaseInfo(
                definition.id,
                metadata.localizedPrice,
                metadata.isoCurrencyCode,
                quantity > 0 ? quantity : 1);

            var receipt = ParseReceipt(receiptJson);
            if (receipt == null)
            {
                Debug.LogWarning($"{LogPrefix} '{definition.id}' reported without store validation: " +
                                 "the product carries no readable receipt.");
                return purchase;
            }

            var isSubscription = definition.type == ProductType.Subscription;
            switch (receipt.Store)
            {
                case StoreGooglePlay:
                    purchase.SetGoogleValidation(receipt.Payload, isSubscription);
                    break;

                case StoreAppleAppStore:
                case StoreMacAppStore:
                    // The legacy receipt has no JWS; its payload is the base64 app receipt.
                    var id = string.IsNullOrEmpty(transactionId) ? receipt.TransactionID : transactionId;
                    var appReceipt = string.IsNullOrEmpty(appleReceipt) ? receipt.Payload : appleReceipt;
                    purchase.SetAppleValidation(id, appleJws, appReceipt, isSubscription);
                    break;

                case StoreAmazon:
                    purchase.SetAmazonValidation(receipt.Payload);
                    break;

                case StoreFake:
                    // Editor play mode: nothing to validate, the CAS Editor stub only logs.
                    break;

                default:
                    Debug.LogWarning($"{LogPrefix} '{definition.id}' reported without store validation: " +
                                     $"store '{receipt.Store}' is not one CAS validates.");
                    break;
            }

            return purchase;
        }

        private static UnifiedReceipt ParseReceipt(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var receipt = JsonUtility.FromJson<UnifiedReceipt>(json);
                return string.IsNullOrEmpty(receipt?.Store) ? null : receipt;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
