﻿using System;
using System.IO;
using System.Threading;
using ClassifiedAds.BackgroundServer.ConfigurationOptions;
using ClassifiedAds.BackgroundServer.HostedServices;
using ClassifiedAds.Domain.Infrastructure.MessageBrokers;
using ClassifiedAds.Infrastructure.HealthChecks;
using ClassifiedAds.Infrastructure.Notification.Web;
using ClassifiedAds.Modules.Notification.Contracts.DTOs;
using ClassifiedAds.Modules.Notification.Services;
using ClassifiedAds.Modules.Storage.DTOs;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassifiedAds.BackgroundServer
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            Configuration = configuration;

            var appSettings = new AppSettings();
            Configuration.Bind(appSettings);
            AppSettings = appSettings;

            var validationResult = appSettings.Validate();
            if (validationResult.Failed)
            {
                throw new Exception(validationResult.FailureMessage);
            }
        }

        public IConfiguration Configuration { get; }

        private AppSettings AppSettings { get; set; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<AppSettings>(Configuration);

            if (AppSettings.CheckDependency.Enabled)
            {
                var hosts = AppSettings.CheckDependency.Host.Split(',');
                foreach (var host in hosts)
                {
                    NetworkPortCheck.Wait(host, 5);
                }
            }

            services.AddHangfire(x =>
            {
                var options = new SqlServerStorageOptions
                {
                    PrepareSchemaIfNecessary = true,
                };
                x.UseSqlServerStorage(AppSettings.ConnectionStrings.ClassifiedAds, options);
            });

            services.AddDateTimeProvider();

            services.AddNotificationModule(AppSettings.MessageBroker, AppSettings.Notification, AppSettings.ConnectionStrings.ClassifiedAds)
                    .AddApplicationServices();

            services.AddMessageBusReceiver<FileUploadedEvent>(AppSettings.MessageBroker);
            services.AddMessageBusReceiver<FileDeletedEvent>(AppSettings.MessageBroker);
            services.AddMessageBusReceiver<EmailMessageCreatedEvent>(AppSettings.MessageBroker);
            services.AddMessageBusReceiver<SmsMessageCreatedEvent>(AppSettings.MessageBroker);

            services.AddHostedService<ResendEmailHostedService>();
            services.AddHostedService<ResendSmsHostedService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseHangfireServer();

            RunMessageBrokerReceivers(app.ApplicationServices.CreateScope().ServiceProvider);

            app.Run(async (context) =>
            {
                await context.Response.WriteAsync("Hello Hangfire Server!");
            });
        }

        private void RunMessageBrokerReceivers(IServiceProvider serviceProvider)
        {
            var fileUploadedMessageQueueReceiver = serviceProvider.GetService<IMessageReceiver<FileUploadedEvent>>();
            var fileDeletedMessageQueueReceiver = serviceProvider.GetService<IMessageReceiver<FileDeletedEvent>>();
            var emailMessageCreatedMessageQueueReceiver = serviceProvider.GetService<IMessageReceiver<EmailMessageCreatedEvent>>();
            var smsMessageCreatedMessageQueueReceiver = serviceProvider.GetService<IMessageReceiver<SmsMessageCreatedEvent>>();

            var notification = serviceProvider.GetService<IWebNotification>();
            var endpoint = $"{AppSettings.NotificationServer.Endpoint}/SimulatedLongRunningTaskHub";

            fileUploadedMessageQueueReceiver?.Receive(data =>
            {
                Thread.Sleep(5000); // simulate long running task

                string message = data.FileEntry.Id.ToString();

                notification.Send(endpoint, "SendTaskStatus", new { Step = $"{AppSettings.MessageBroker.Provider} - File Uploaded", Message = message });
            });

            fileDeletedMessageQueueReceiver?.Receive(data =>
            {
                Thread.Sleep(5000); // simulate long running task

                string message = data.FileEntry.Id.ToString();

                notification.Send(endpoint, "SendTaskStatus", new { Step = $"{AppSettings.MessageBroker.Provider} - File Deleted", Message = message });
            });

            var emailMessageService = serviceProvider.GetService<EmailMessageService>();

            emailMessageCreatedMessageQueueReceiver?.Receive(data =>
            {
                string message = data.Id.ToString();

                try
                {
                    emailMessageService.SendEmailMessage(data.Id);
                }
                catch (Exception ex)
                { }

                notification.Send(endpoint, "SendTaskStatus", new { Step = $"Send Email", Message = message });
            });

            var smsMessageService = serviceProvider.GetService<SmsMessageService>();

            smsMessageCreatedMessageQueueReceiver?.Receive(data =>
            {
                string message = data.Id.ToString();

                try
                {
                    smsMessageService.SendSmsMessage(data.Id);
                }
                catch (Exception ex)
                { }

                notification.Send(endpoint, "SendTaskStatus", new { Step = $"Send Sms", Message = message });
            });
        }
    }
}
