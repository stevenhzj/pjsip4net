using System;
using System.Collections.Generic;
using System.Linq;
using pjsip4net.Accounts;
using pjsip4net.Core;
using pjsip4net.Core.Configuration;
using pjsip4net.Core.Container;
using pjsip4net.Core.Data;
using pjsip4net.Core.Interfaces;
using pjsip4net.Core.Interfaces.ApiProviders;
using pjsip4net.Core.Utils;
using pjsip4net.Interfaces;
using pjsip4net.Media;

namespace pjsip4net.Configuration
{
    public static class BuildUserAgent
    {
        /// <summary>
        /// 
        /// </summary>
        /// <exception cref="InvalidOperationException"/>
        /// <exception cref="PjsipErrorException"/>
        /// <exception cref="ArgumentNullException"/>
        public static Configure Build(this Configure cfg)
        {
            try
            {
                cfg.Container.RegisterAsSingleton(cfg.Container);
            }
            catch (ContainerException)//it might be already configured somewhere up
            {
            }

            cfg.AddComponentConfigurator(new DefaultComponentConfigurator())
                .AddComponentConfigurator(new MediaComponentConfigurator()).RunConfigurators();

            var localRegistry = cfg.Container.Get<ILocalRegistry>();

            var basicApiProvider = cfg.Container.Get<IBasicApiProvider>();
            if (basicApiProvider == null)
                throw new InvalidOperationException("There is no API version configured");

            basicApiProvider.CreatePjsua();

            localRegistry.Config = basicApiProvider.GetDefaultUaConfig();
            localRegistry.LoggingConfig = basicApiProvider.GetDefaultLoggingConfig();
            localRegistry.MediaConfig = cfg.Container.Get<IMediaApiProvider>().GetDefaultConfig();

            cfg.RunConfigurationProviders();

            var accApi = cfg.Container.Get<IAccountApiProvider>();
            var tptApi = cfg.Container.Get<ITransportApiProvider>();
            if (localRegistry.AccountConfigs != null)
                localRegistry.AccountConfigs.Union(cfg.GetConfiguredAccounts(accApi));
            else
                localRegistry.AccountConfigs = cfg.GetConfiguredAccounts(accApi);
            var tptConfig = cfg.GetConfiguredTransport(tptApi);
            if (tptConfig != null)
                localRegistry.TransportConfig = tptConfig;

            var imMgr = cfg.Container.Get<IImManager>();
            Helper.GuardNotNull(imMgr);
            using (imMgr.InitializationScope())
            { }

            var callMgr = cfg.Container.Get<ICallManagerInternal>();
            Helper.GuardNotNull(callMgr);
            using (callMgr.InitializationScope())
            {
                callMgr.MaxCalls = localRegistry.Config.MaxCalls;
            }

            var accMgr = cfg.Container.Get<IAccountManagerInternal>();
            Helper.GuardNotNull(accMgr);
            using (accMgr.InitializationScope())
            { }

            var mediaMgr = cfg.Container.Get<IMediaManagerInternal>();
            Helper.GuardNotNull(mediaMgr);
            using (mediaMgr.InitializationScope())
            { }

            var ua = cfg.Container.Get<ISipUserAgentInternal>();
            Helper.GuardNotNull(ua);
            ua.SetManagers(imMgr, callMgr, accMgr, mediaMgr);

            basicApiProvider.InitPjsua(localRegistry.Config, localRegistry.LoggingConfig, localRegistry.MediaConfig);
            //basicApiProvider.Start();

            return cfg;
        }

        public static ISipUserAgent Start(this Configure cfg)
        {
            var localRegistry = cfg.Container.Get<ILocalRegistry>();
            if (localRegistry.Config == null)
                throw new InvalidOperationException("You should call Build first to build up user agent infrastructure.");

            var transportFactory = cfg.Container.Get<IVoIPTransportFactory>();
            localRegistry.SipTransport = transportFactory.CreateTransport(localRegistry.TransportConfig.Part1,
                                                                          localRegistry.TransportConfig.Part2);
            using (localRegistry.SipTransport.InitializationScope())
                localRegistry.SipTransport.SetConfig(localRegistry.TransportConfig.Part2);
            var tptApiProvider = cfg.Container.Get<ITransportApiProvider>();
            localRegistry.SipTransport.SetId(
                tptApiProvider.CreateTransportAndGetId(localRegistry.SipTransport.TransportType,
                                                       localRegistry.SipTransport.Config));
            localRegistry.RtpTransport = transportFactory.CreateTransport(TransportType.Udp);
            using (localRegistry.RtpTransport.InitializationScope())
                localRegistry.RtpTransport.Config.Port = 4000;

            var mediaApiProvider = cfg.Container.Get<IMediaApiProvider>();
            mediaApiProvider.CreateMediaTransport(localRegistry.RtpTransport.Config);

            var basicApiProvider = cfg.Container.Get<IBasicApiProvider>();
            basicApiProvider.Start();

            var objectFactory = cfg.Container.Get<IObjectFactory>();
            var accMgr = cfg.Container.Get<IAccountManager>();
            //always register local account first
            var account = objectFactory.Create<Account>();
            using (account.InitializationScope())
            {
                account.IsLocal = true;
                account.Transport = localRegistry.SipTransport;
            }
            accMgr.RegisterAccount(account, true);
            //TODO: generalize transport injection

            //register configured accounts
            IEnumerable<AccountConfig> preconfiguredAccounts = localRegistry.AccountConfigs;
            if (preconfiguredAccounts.Count() > 0)
                foreach (var accCfg in preconfiguredAccounts)
                {
                    var acc = objectFactory.Create<Account>();
                    using (acc.InitializationScope())
                    {
                        acc.SetConfig(accCfg);
                        acc.Transport = localRegistry.SipTransport;
                    }
                    accMgr.RegisterAccount(acc, acc.Default);
                }

            return cfg.Container.Get<ISipUserAgent>();
        }
    }
}