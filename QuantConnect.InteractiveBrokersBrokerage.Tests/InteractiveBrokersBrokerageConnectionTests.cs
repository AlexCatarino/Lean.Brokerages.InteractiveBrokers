/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Orders;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    [TestFixture]
    public class InteractiveBrokersBrokerageConnectionTests
    {
        private static readonly FieldInfo HeartBeatTimeLimitField =
            typeof(InteractiveBrokersBrokerage).GetField("_heartBeatTimeLimit", BindingFlags.NonPublic | BindingFlags.Static);

        private TimeSpan _originalHeartBeatTimeLimit;

        [SetUp]
        public void SetUp()
        {
            // the heart beat is skipped after 23:00 local time because of the gateway daily restart, which would
            // make the tests below assert nothing when they happen to run in that hour. Push the limit out so the
            // wall clock stops deciding which branch is exercised.
            _originalHeartBeatTimeLimit = (TimeSpan)HeartBeatTimeLimitField.GetValue(null);
            HeartBeatTimeLimitField.SetValue(null, TimeSpan.FromDays(1));
        }

        [TearDown]
        public void TearDown()
        {
            HeartBeatTimeLimitField.SetValue(null, _originalHeartBeatTimeLimit);
        }

        // The heart beat is the only check that runs unconditionally, so it is what has to notice a connection
        // that never came back. It used to report a healthy beat whenever it was not connected, which meant the
        // detector was switched off by the very condition it exists to detect: a gateway restart that failed to
        // reconnect kept the algorithm running for three days, silently rejecting every order, because no
        // disconnect was ever reported to the brokerage message handler.
        [Test]
        public void HeartBeatReportsFailureWhenTheConnectionIsLostWithoutAnExpectedReason()
        {
            using var brokerage = new InteractiveBrokersBrokerage();
            // no client at all, so IsConnected is false: this is the state the deployment sat in for three days
            SetPrivateField(brokerage, "_ibAutomater", CreateInertAutomater());

            Assert.IsFalse(InvokeIsConnected(brokerage), "the fixture must start from a disconnected state");

            // IsWithinScheduledServerResetTimes() reads the wall clock and is the one input that cannot be
            // injected. Skipping is better than asserting the opposite branch here: it keeps the test from
            // silently passing without exercising the case it exists for.
            if (InvokeIsWithinExpectedDisconnectionWindow(brokerage, out var reason))
            {
                Assert.Ignore($"cannot reproduce an unexpected disconnection right now: {reason}");
            }

            Assert.IsFalse(InvokeHeartBeat(brokerage),
                "a lost connection that nothing accounts for must be reported as a failed beat");
        }

        // The counterpart of the test above and the reason it cannot simply alarm on every dropped connection:
        // the gateway restarts every night at 23:45 and the socket is torn down on purpose each time. Those
        // windows reconnect within about a minute and must not be reported, otherwise every deployment would be
        // killed nightly.
        [TestCase("_isDisposeCalled", TestName = "HeartBeatStaysQuietWhileDisposing")]
        [TestCase("IsConnecting", TestName = "HeartBeatStaysQuietWhileConnecting")]
        public void HeartBeatDoesNotReportAnExpectedDisconnection(string expectedReasonField)
        {
            using var brokerage = new InteractiveBrokersBrokerage();

            if (expectedReasonField == "IsConnecting")
            {
                // a connection attempt holds IsConnected false for as long as it runs, which is minutes when it
                // needs a 2FA confirmation
                GetStateManager(brokerage).IsConnecting = true;
            }
            else
            {
                SetPrivateField(brokerage, expectedReasonField, true);
            }

            Assert.IsFalse(InvokeIsConnected(brokerage));
            Assert.IsTrue(InvokeIsWithinExpectedDisconnectionWindow(brokerage, out _));
            Assert.IsTrue(InvokeHeartBeat(brokerage), "an expected disconnection must not be reported as a failed beat");
        }

        // DefaultBrokerageMessageHandler disposes its pending countdown and starts a new one every time it
        // receives a disconnect message, so repeating it while still down would keep pushing the algorithm
        // shutdown into the future and never stop anything. It has to be raised once per disconnection.
        [Test]
        public void DisconnectIsReportedOncePerDisconnection()
        {
            using var brokerage = new InteractiveBrokersBrokerage();
            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, message) => messages.Add(message);

            InvokeOnDisconnected(brokerage, "first");
            InvokeOnDisconnected(brokerage, "second");
            InvokeOnDisconnected(brokerage, "third");

            Assert.AreEqual(1, messages.Count(x => x.Type == BrokerageMessageType.Disconnect));
            Assert.AreEqual("first", messages.Single(x => x.Type == BrokerageMessageType.Disconnect).Message);
        }

        // ...and once we are back the next disconnection is a new episode that has to be reported again,
        // otherwise the safety net only ever works once per deployment.
        [Test]
        public void ReconnectingReArmsTheDisconnectReport()
        {
            using var brokerage = new InteractiveBrokersBrokerage();
            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, message) => messages.Add(message);

            InvokeOnDisconnected(brokerage, "lost");
            InvokeOnReconnected(brokerage, "back");
            InvokeOnDisconnected(brokerage, "lost again");

            Assert.AreEqual(2, messages.Count(x => x.Type == BrokerageMessageType.Disconnect));
            Assert.AreEqual(1, messages.Count(x => x.Type == BrokerageMessageType.Reconnect));
            CollectionAssert.AreEqual(
                new[] { BrokerageMessageType.Disconnect, BrokerageMessageType.Reconnect, BrokerageMessageType.Disconnect },
                messages.Select(x => x.Type));
        }

        // The transaction handler invalidates the order itself when PlaceOrder reports a failure, but only with a
        // generic "Brokerage failed to place orders: [20]" that never says why. The brokerage invalidates it with
        // the actual cause instead, and reports success so the same rejection is not raised twice.
        [Test]
        public void PlaceOrderWhenDisconnectedInvalidatesTheOrderWithTheReason()
        {
            using var brokerage = new InteractiveBrokersBrokerage();
            var messages = new List<BrokerageMessageEvent>();
            var orderEvents = new List<OrderEvent>();
            brokerage.Message += (_, message) => messages.Add(message);
            brokerage.OrdersStatusChanged += (_, events) => orderEvents.AddRange(events);

            Assert.IsFalse(brokerage.IsConnected);

            var order = new MarketOrder(Symbols.SPY, -19454, DateTime.UtcNow);
            brokerage.PlaceOrder(order);

            var invalidated = orderEvents.Single();
            Assert.AreEqual(OrderStatus.Invalid, invalidated.Status);
            Assert.AreEqual(order.Id, invalidated.OrderId);
            Assert.AreEqual("Orders cannot be submitted when disconnected.", invalidated.Message);
            // the brokerage level warning is still raised, it is what support greps for by code
            Assert.AreEqual(1, messages.Count(x => x.Code == "PlaceOrderWhenDisconnected"));
        }

        /// <summary>
        /// An IBAutomater that is only ever asked about the scheduled reset times, it is never started so no
        /// gateway process is involved.
        /// </summary>
        private static QuantConnect.IBAutomater.IBAutomater CreateInertAutomater()
        {
            return new QuantConnect.IBAutomater.IBAutomater(
                ibDirectory: string.Empty,
                ibVersion: string.Empty,
                userName: string.Empty,
                password: string.Empty,
                tradingMode: "paper",
                portNumber: 4002,
                exportIbGatewayLogs: false);
        }

        private static bool InvokeHeartBeat(InteractiveBrokersBrokerage brokerage)
        {
            // a tiny wait keeps the test fast, the value only drives the internal wait handles
            return (bool)InvokePrivate(brokerage, "HeartBeat", new object[] { 1 });
        }

        private static bool InvokeIsWithinExpectedDisconnectionWindow(InteractiveBrokersBrokerage brokerage, out string reason)
        {
            var arguments = new object[] { null };
            var result = (bool)InvokePrivate(brokerage, "IsWithinExpectedDisconnectionWindow", arguments);
            reason = (string)arguments[0];
            return result;
        }

        private static void InvokeOnDisconnected(InteractiveBrokersBrokerage brokerage, string message)
        {
            InvokePrivate(brokerage, "OnDisconnected", new object[] { message });
        }

        private static void InvokeOnReconnected(InteractiveBrokersBrokerage brokerage, string message)
        {
            InvokePrivate(brokerage, "OnReconnected", new object[] { message });
        }

        private static bool InvokeIsConnected(InteractiveBrokersBrokerage brokerage)
        {
            return brokerage.IsConnected;
        }

        private static InteractiveBrokersStateManager GetStateManager(InteractiveBrokersBrokerage brokerage)
        {
            return (InteractiveBrokersStateManager)typeof(InteractiveBrokersBrokerage)
                .GetField("_stateManager", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(brokerage);
        }

        private static object InvokePrivate(object instance, string name, object[] arguments)
        {
            return instance.GetType()
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(instance, arguments);
        }

        private static void SetPrivateField(object instance, string name, object value)
        {
            instance.GetType()
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(instance, value);
        }
    }
}
