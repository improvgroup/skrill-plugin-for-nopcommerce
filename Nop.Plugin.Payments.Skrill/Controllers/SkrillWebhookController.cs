﻿using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Nop.Core.Domain.Orders;
using Nop.Core.Http.Extensions;
using Nop.Plugin.Payments.Skrill.Domain;
using Nop.Plugin.Payments.Skrill.Services;
using Nop.Services.Common;
using Nop.Services.Customers;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Web.Framework.Controllers;

namespace Nop.Plugin.Payments.Skrill.Controllers
{
    public class SkrillWebhookController : BasePaymentController
    {
        #region Fields

        private readonly ICustomerService _customerService;
        private readonly IGenericAttributeService _genericAttributeService;
        private readonly IPaymentService _paymentService;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly IOrderService _orderService;
        private readonly ServiceManager _serviceManager;

        #endregion

        #region Ctor

        public SkrillWebhookController(ICustomerService customerService,
            IGenericAttributeService genericAttributeService,
            IPaymentService paymentService,
            IOrderProcessingService orderProcessingService,
            IOrderService orderService,
            ServiceManager serviceManager)
        {
            _customerService = customerService;
            _genericAttributeService = genericAttributeService;
            _paymentService = paymentService;
            _orderProcessingService = orderProcessingService;
            _orderService = orderService;
            _serviceManager = serviceManager;
        }

        #endregion

        #region Methods

        public IActionResult OrderPaidWebhook(Guid? transaction_id)
        {
            if (_serviceManager.GetPaymentFlowType() == PaymentFlowType.Redirection)
                return Content("Invalid");

            if (!transaction_id.HasValue)
                return Content("Invalid");

            var processPaymentRequest = HttpContext.Session.Get<ProcessPaymentRequest>(Defaults.PaymentRequestSessionKey);
            if (processPaymentRequest == null)
                return Content("Invalid");

            _paymentService.GenerateOrderGuid(processPaymentRequest);

            if (processPaymentRequest.OrderGuid != transaction_id.Value)
                return Content("Invalid");

            return Content("Ok");
        }

        [HttpPost]
        public IActionResult QuickCheckoutWebhook()
        {
            try
            {
                //validate request
                var isValid = _serviceManager.ValidateWebhookRequest(Request.Form);
                if (!isValid)
                    return BadRequest();

                var orderGuid = Guid.Parse(Request.Form["transaction_id"]);
                var order = _orderService.GetOrderByGuid(orderGuid);
                if (order == null && _serviceManager.GetPaymentFlowType() == PaymentFlowType.Inline)
                {
                    //order isn't placed
                    //try save the Skrill transaction_id for further processing

                    if (int.TryParse(Request.Form["nop_customer_id"].ToString(), out var customerId))
                    {
                        var customer = _customerService.GetCustomerById(customerId);
                        if (customer != null)
                        {
                            //status 2 - payment transaction was successful
                            if (Request.Form["status"].ToString().ToLower() == "2" && Request.Form.TryGetValue("mb_transaction_id", out var transactionId))
                                _genericAttributeService.SaveAttribute(customer, Defaults.PaymentTransactionIdAttribute, transactionId.ToString());
                        }
                    }
                }
                else
                {
                    if (order == null)
                        return Ok();

                    //add order note
                    var details = Request.Form.Aggregate(string.Empty, (message, parameter) => $"{message}{parameter.Key}: {parameter.Value}; ");
                    _orderService.InsertOrderNote(new OrderNote
                    {
                        OrderId = order.Id,
                        Note = $"Webhook details: {Environment.NewLine}{details}",
                        DisplayToCustomer = false,
                        CreatedOnUtc = DateTime.UtcNow
                    });

                    //check transaction status
                    switch (Request.Form["status"].ToString().ToLower())
                    {
                        //order cancelled
                        case "-3":
                        case "-2":
                        case "-1":
                            if (Enum.TryParse<FailedReasonCode>(Request.Form["failed_reason_code"], out var failedReason))
                            {
                                _orderService.InsertOrderNote(new OrderNote
                                {
                                    OrderId = order.Id,
                                    Note = $"Order cancelled. Reason: {failedReason}",
                                    DisplayToCustomer = false,
                                    CreatedOnUtc = DateTime.UtcNow
                                });
                            }
                            if (_orderProcessingService.CanCancelOrder(order))
                                _orderProcessingService.CancelOrder(order, true);
                            break;

                        //order pending
                        case "0":
                            order.OrderStatus = OrderStatus.Pending;
                            _orderService.UpdateOrder(order);
                            _orderProcessingService.CheckOrderStatus(order);
                            break;

                        //order processed
                        case "2":
                            if (_orderProcessingService.CanMarkOrderAsPaid(order))
                            {
                                if (Request.Form.TryGetValue("mb_transaction_id", out var transactionId))
                                    order.CaptureTransactionId = transactionId;
                                _orderService.UpdateOrder(order);
                                _orderProcessingService.MarkOrderAsPaid(order);
                            }
                            break;
                    }
                }
            }
            catch { }

            return Ok();
        }

        [HttpPost]
        public IActionResult RefundWebhook()
        {
            try
            {
                //validate request
                var isValid = _serviceManager.ValidateWebhookRequest(Request.Form);
                if (!isValid)
                    return BadRequest();

                //try to get an order for this transaction
                var orderGuid = Guid.Parse(Request.Form["transaction_id"]);
                var order = _orderService.GetOrderByGuid(orderGuid);
                if (order == null)
                    return Ok();

                //add order note
                var details = Request.Form.Aggregate(string.Empty, (message, parameter) => $"{message}{parameter.Key}: {parameter.Value}; ");
                _orderService.InsertOrderNote(new OrderNote
                {
                    OrderId = order.Id,
                    Note = $"Webhook details: {Environment.NewLine}{details}",
                    DisplayToCustomer = false,
                    CreatedOnUtc = DateTime.UtcNow
                });

                //check transaction status
                switch (Request.Form["status"].ToString().ToLower())
                {
                    //refund processed
                    case "2":
                        //ensure that this refund has not been processed before
                        var refundGuid = _genericAttributeService.GetAttribute<string>(order, Defaults.RefundGuidAttribute);
                        if (refundGuid?.Equals(Request.Form["refund_guid"], StringComparison.InvariantCultureIgnoreCase) ?? false)
                            break;

                        if (decimal.TryParse(Request.Form["mb_amount"], out var refundedAmount) &&
                            _orderProcessingService.CanPartiallyRefundOffline(order, refundedAmount))
                        {
                            _orderProcessingService.PartiallyRefundOffline(order, refundedAmount);
                        }
                        break;
                }
            }
            catch { }

            return Ok();
        }

        #endregion
    }
}