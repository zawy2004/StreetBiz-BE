/*
  Read-only verification for CART-01, ORD-01..04 and SORD-01..04.
  This script does not create, alter, delete or reset data.
*/
SET NOCOUNT ON;

DECLARE @Required TABLE
(
    table_name sysname NOT NULL,
    column_name sysname NOT NULL
);

INSERT INTO @Required (table_name, column_name)
VALUES
('ShoppingCarts', 'cart_id'), ('ShoppingCarts', 'customer_user_id'),
('ShoppingCarts', 'storefront_id'), ('ShoppingCarts', 'cart_status'),
('ShoppingCartItems', 'cart_item_id'), ('ShoppingCartItems', 'cart_id'),
('ShoppingCartItems', 'menu_item_id'), ('ShoppingCartItems', 'quantity'),
('Orders', 'order_id'), ('Orders', 'order_code'), ('Orders', 'customer_user_id'),
('Orders', 'storefront_id'), ('Orders', 'order_status'),
('Orders', 'subtotal_amount'), ('Orders', 'total_amount'),
('Orders', 'placed_at'), ('Orders', 'completed_at'),
('OrderItems', 'order_item_id'), ('OrderItems', 'order_id'),
('OrderItems', 'item_name_snapshot'), ('OrderItems', 'unit_price_snapshot'),
('OrderItems', 'quantity'), ('OrderStatusHistory', 'history_id'),
('OrderStatusHistory', 'order_id'), ('OrderStatusHistory', 'from_status'),
('OrderStatusHistory', 'to_status'), ('OrderStatusHistory', 'changed_by'),
('PaymentTransactions', 'transaction_id'), ('PaymentTransactions', 'idempotency_key'),
('PaymentTransactions', 'order_id'), ('PaymentTransactions', 'provider'),
('PaymentTransactions', 'provider_reference'), ('PaymentTransactions', 'amount'),
('PaymentTransactions', 'transaction_status'),
('PaymentCallbackEvents', 'callback_event_id'), ('PaymentCallbackEvents', 'provider'),
('PaymentCallbackEvents', 'provider_reference'), ('PaymentCallbackEvents', 'transaction_id'),
('PaymentCallbackEvents', 'raw_payload'), ('PaymentCallbackEvents', 'signature_valid'),
('PaymentCallbackEvents', 'processing_result'),
('RefundTransactions', 'refund_id'), ('RefundTransactions', 'order_id'),
('RefundTransactions', 'payment_transaction_id'), ('RefundTransactions', 'idempotency_key'),
('RefundTransactions', 'amount'), ('RefundTransactions', 'refund_reason'),
('RefundTransactions', 'provider'), ('RefundTransactions', 'refund_status'),
('Notifications', 'notification_id'), ('Notifications', 'user_id'),
('Notifications', 'related_entity_type'), ('Notifications', 'related_entity_id');

SELECT r.table_name, r.column_name, 'MISSING' AS verification_result
FROM @Required AS r
LEFT JOIN INFORMATION_SCHEMA.COLUMNS AS c
  ON c.TABLE_SCHEMA = 'dbo'
 AND c.TABLE_NAME = r.table_name
 AND c.COLUMN_NAME = r.column_name
WHERE c.COLUMN_NAME IS NULL
ORDER BY r.table_name, r.column_name;

SELECT
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM @Required AS r
        LEFT JOIN INFORMATION_SCHEMA.COLUMNS AS c
          ON c.TABLE_SCHEMA = 'dbo'
         AND c.TABLE_NAME = r.table_name
         AND c.COLUMN_NAME = r.column_name
        WHERE c.COLUMN_NAME IS NULL
    )
    THEN 'FAILED: required order columns are missing'
    ELSE 'PASSED: all required order columns exist'
    END AS orders_schema_verification;
