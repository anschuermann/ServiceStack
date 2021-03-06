﻿using Funq;
using NUnit.Framework;
using ServiceStack.Configuration;
using ServiceStack.FluentValidation;
using ServiceStack.Messaging;
using ServiceStack.Messaging.Redis;
using ServiceStack.Redis;
using ServiceStack.Text;
using ServiceStack.Validation;

namespace ServiceStack.Common.Tests.Messaging
{
    public class AnyTestMq
    {
        public int Id { get; set; }
    }

    public class AnyTestMqResponse
    {
        public int CorrelationId { get; set; }
    }

    public class PostTestMq
    {
        public int Id { get; set; }
    }

    public class PostTestMqResponse
    {
        public int CorrelationId { get; set; }
    }

    public class ValidateTestMq
    {
        public int Id { get; set; }
    }

    public class ValidateTestMqResponse
    {
        public int CorrelationId { get; set; }

        public ResponseStatus ResponseStatus { get; set; }
    }

    public class ValidateTestMqValidator : AbstractValidator<ValidateTestMq>
    {
        public ValidateTestMqValidator()
        {
            RuleFor(x => x.Id)
                .GreaterThanOrEqualTo(0)
                .WithErrorCode("PositiveIntegersOnly");
        }
    }

    public class TestMqService : IService
    {
        public object Any(AnyTestMq request)
        {
            return new AnyTestMqResponse { CorrelationId = request.Id };
        }

        public object Post(PostTestMq request)
        {
            return new PostTestMqResponse { CorrelationId = request.Id };
        }

        public object Post(ValidateTestMq request)
        {
            return new ValidateTestMqResponse { CorrelationId = request.Id };
        }
    }

    public class AppHost : AppHostHttpListenerBase
    {
        public AppHost()
            : base("Service Name", typeof(AnyTestMq).Assembly) { }

        public override void Configure(Container container)
        {
            Plugins.Add(new ValidationFeature());
            container.RegisterValidators(typeof(ValidateTestMqValidator).Assembly);

            var appSettings = new AppSettings();
            container.Register<IRedisClientsManager>(c => new PooledRedisClientManager(
                new[] { appSettings.GetString("Redis.Host") ?? "localhost" }));
            container.Register<IMessageService>(c => new RedisMqServer(c.Resolve<IRedisClientsManager>()));

            var mqServer = (RedisMqServer)container.Resolve<IMessageService>();
            mqServer.RegisterHandler<AnyTestMq>(ServiceController.ExecuteMessage);
            mqServer.RegisterHandler<PostTestMq>(ServiceController.ExecuteMessage);
            mqServer.RegisterHandler<ValidateTestMq>(ServiceController.ExecuteMessage);

            mqServer.Start();
        }
    }

    [TestFixture]
    public class RedisMqServerTests
    {
        private const string ListeningOn = "http://*:1337/";
        public const string Host = "http://localhost:1337";
        private const string BaseUri = Host + "/";

        AppHost appHost;

        [TestFixtureSetUp]
        public void TestFixtureSetUp()
        {
            appHost = new AppHost();
            appHost.Init();
            appHost.Start(ListeningOn);

            using (var redis = appHost.TryResolve<IRedisClientsManager>().GetClient())
                redis.FlushAll();
        }

        [TestFixtureTearDown]
        public void TestFixtureTearDown()
        {
            appHost.Dispose();
        }

        [Test]
        public void Can_Publish_to_AnyTestMq_Service()
        {
            using (var mqFactory = appHost.TryResolve<IMessageFactory>())
            {
                var request = new AnyTestMq { Id = 1 };
                mqFactory.CreateMessageProducer().Publish(request);
                var msg = mqFactory.CreateMessageQueueClient().Get(QueueNames<AnyTestMqResponse>.In, null)
                    .ToMessage<AnyTestMqResponse>();
                Assert.That(msg.GetBody().CorrelationId, Is.EqualTo(request.Id));
            }
        }

        [Test]
        public void Can_Publish_to_PostTestMq_Service()
        {
            using (var mqFactory = appHost.TryResolve<IMessageFactory>())
            {
                var request = new PostTestMq { Id = 2 };
                mqFactory.CreateMessageProducer().Publish(request);
                var msg = mqFactory.CreateMessageQueueClient().Get(QueueNames<PostTestMqResponse>.In, null)
                    .ToMessage<PostTestMqResponse>();
                Assert.That(msg.GetBody().CorrelationId, Is.EqualTo(request.Id));
            }
        }

        [Test]
        public void SendOneWay_calls_AnyTestMq_Service_via_MQ()
        {
            var client = new JsonServiceClient(BaseUri);
            var request = new AnyTestMq { Id = 3 };

            client.SendOneWay(request);

            using (var mqFactory = appHost.TryResolve<IMessageFactory>())
            {
                var msg = mqFactory.CreateMessageQueueClient().Get(QueueNames<AnyTestMqResponse>.In, null)
                    .ToMessage<AnyTestMqResponse>();
                Assert.That(msg.GetBody().CorrelationId, Is.EqualTo(request.Id));
            }
        }

        [Test]
        public void SendOneWay_calls_PostTestMq_Service_via_MQ()
        {
            var client = new JsonServiceClient(BaseUri);
            var request = new PostTestMq { Id = 4 };

            client.SendOneWay(request);

            using (var mqFactory = appHost.TryResolve<IMessageFactory>())
            {
                var msg = mqFactory.CreateMessageQueueClient().Get(QueueNames<PostTestMqResponse>.In, null)
                    .ToMessage<PostTestMqResponse>();
                Assert.That(msg.GetBody().CorrelationId, Is.EqualTo(request.Id));
            }
        }

        [Test]
        public void Does_execute_validation_filters()
        {
            using (var mqFactory = appHost.TryResolve<IMessageFactory>())
            {
                var request = new ValidateTestMq { Id = -10 };
                mqFactory.CreateMessageProducer().Publish(request);
                var msg = mqFactory.CreateMessageQueueClient().Get(QueueNames<ValidateTestMqResponse>.Dlq, null)
                    .ToMessage<ValidateTestMqResponse>();

                msg.GetBody().PrintDump();
                Assert.That(msg.GetBody().ResponseStatus.ErrorCode, Is.EqualTo("PositiveIntegersOnly"));

                request = new ValidateTestMq { Id = 10 };
                mqFactory.CreateMessageProducer().Publish(request);
                msg = mqFactory.CreateMessageQueueClient().Get(QueueNames<ValidateTestMqResponse>.In, null)
                    .ToMessage<ValidateTestMqResponse>();
                Assert.That(msg.GetBody().CorrelationId, Is.EqualTo(request.Id));
            }
        }
    }
}