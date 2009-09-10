// ============================================================================
// FileName: MainConsole.cs
//
// Description:
// Main console display for the demonstration SIP Proxy.
//
// Author(s):
// Aaron Clauson
//
// History:
// 13 Aug 2006	Aaron Clauson	Created.
//
// License: 
// This software is licensed under the BSD License http://www.opensource.org/licenses/bsd-license.php
//
// Copyright (c) 2006 Aaron Clauson (aaronc@blueface.ie), Blue Face Ltd, Dublin, Ireland (www.blueface.ie)
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that 
// the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. 
// Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following 
// disclaimer in the documentation and/or other materials provided with the distribution. Neither the name of Blue Face Ltd. 
// nor the names of its contributors may be used to endorse or promote products derived from this software without specific 
// prior written permission. 
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, 
// BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
// IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, 
// OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
// OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, 
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
// POSSIBILITY OF SUCH DAMAGE.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Web;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using SIPSorcery.AppServer.DialPlan;
using SIPSorcery.CRM;
using SIPSorcery.Net;
using SIPSorcery.Servers;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.SIPMonitor;
using SIPSorcery.SIPProxy;
using SIPSorcery.SIPRegistrar;
using SIPSorcery.SIPRegistrationAgent;
using SIPSorcery.Sys;
using log4net;

namespace SIPSorcery.SIPAppServer {
    public class SIPAppServerDaemon {

        private static ILog logger = SIPAppServerState.logger;
        private static ILog dialPlanLogger = AppState.GetLogger("dialplan");

        private XmlNode m_sipAppServerSocketsNode = SIPAppServerState.SIPAppServerSocketsNode;

        private static bool m_sipProxyEnabled = (ConfigurationManager.GetSection(SIPProxyState.SIPPROXY_CONFIGNODE_NAME) != null);
        private static bool m_sipMonitorEnabled = (ConfigurationManager.GetSection(SIPMonitorState.SIPMONITOR_CONFIGNODE_NAME) != null);
        private static bool m_sipRegistrarEnabled = (ConfigurationManager.GetSection(SIPRegistrarState.SIPREGISTRAR_CONFIGNODE_NAME) != null);
        private static bool m_sipRegAgentEnabled = (ConfigurationManager.GetSection(SIPRegAgentState.SIPREGAGENT_CONFIGNODE_NAME) != null);
        
        private static int m_monitorEventLoopbackPort = SIPAppServerState.MonitorLoopbackPort;
        private string m_traceDirectory = SIPAppServerState.TraceDirectory;
        private string m_currentDirectory = SIPAppServerState.CurrentDirectory;
        private string m_rubyScriptCommonPath = SIPAppServerState.RubyScriptCommonPath;
        private SIPEndPoint m_outboundProxy = SIPAppServerState.OutboundProxy;
        private XmlNode m_sipCallDispatcherWorkersNode = SIPAppServerState.SIPCallDispatcherWorkersNode;
        private string m_sipCallDispatcherScriptPath = SIPAppServerState.SIPCallDispatcherScriptPath;

        private SIPSorceryPersistor m_sipSorceryPersistor;
        private SIPMonitorEventWriter m_monitorEventWriter;
        private SIPAppServerCore m_appServerCore;
        private SIPCallManager m_callManager;
        private SIPCallDispatcher m_callDispatcher;
        private SIPNotifyManager m_notifyManager;
        private SIPProxyDaemon m_sipProxyDaemon;
        private SIPMonitorDaemon m_sipMonitorDaemon;
        private SIPRegAgentDaemon m_sipRegAgentDaemon;
        private SIPRegistrarDaemon m_sipRegistrarDaemon;

        private SIPTransport m_sipTransport;
        private DialPlanEngine m_dialPlanEngine;
        private ServiceHost m_accessPolicyHost;
        private ServiceHost m_sipProvisioningHost;
        private ServiceHost m_callManagerSvcHost;
        private CustomerSessionManager m_customerSessionManager;
        private IPAddress m_publicIPAddress;

        private StorageTypes m_storageType;
        private string m_connectionString;
        private SIPEndPoint m_appServerEndPoint;
        private string m_callManagerServiceAddress;

        public SIPAppServerDaemon(StorageTypes storageType, string connectionString) {
            m_storageType = storageType;
            m_connectionString = connectionString;
        }

        public SIPAppServerDaemon(
            StorageTypes storageType, 
            string connectionString,
            SIPEndPoint appServerEndPoint,
            string callManagerServiceAddress) {

            m_storageType = storageType;
            m_connectionString = connectionString;
            m_appServerEndPoint = appServerEndPoint;
            m_callManagerServiceAddress = callManagerServiceAddress;
        }

        public void Start() {
            try {
                logger.Debug("pid=" + Process.GetCurrentProcess().Id + ".");
                logger.Debug("os=" + System.Environment.OSVersion + ".");
                logger.Debug("current directory=" + m_currentDirectory + ".");

                m_sipSorceryPersistor = new SIPSorceryPersistor(m_storageType, m_connectionString);
                m_customerSessionManager = new CustomerSessionManager(m_storageType, m_connectionString);

                if (m_sipProxyEnabled) {
                    m_sipProxyDaemon = new SIPProxyDaemon();
                    m_sipProxyDaemon.PublicIPAddressUpdated += (ipAddress) => {
                        if (ipAddress != null && (m_publicIPAddress == null || ipAddress.ToString() != m_publicIPAddress.ToString())) {
                            m_publicIPAddress = ipAddress;
                            DialStringParser.PublicIPAddress = ipAddress;
                            DialPlanScriptHelper.PublicIPAddress = ipAddress;
                        }
                    };

                    m_sipProxyDaemon.Start();
                }

                if (m_sipMonitorEnabled) {
                    m_sipMonitorDaemon = new SIPMonitorDaemon(m_customerSessionManager);
                    m_sipMonitorDaemon.Start();
                }

                if (m_sipRegistrarEnabled) {
                    m_sipRegistrarDaemon = new SIPRegistrarDaemon(
                        m_sipSorceryPersistor.SIPDomainManager.GetDomain,
                        m_sipSorceryPersistor.SIPAccountsPersistor.Get,
                        m_sipSorceryPersistor.SIPRegistrarBindingPersistor,
                        SIPRequestAuthenticator.AuthenticateSIPRequest);
                    m_sipRegistrarDaemon.Start();
                }

                if (m_sipRegAgentEnabled) {
                    m_sipRegAgentDaemon = new SIPRegAgentDaemon(
                        m_sipSorceryPersistor.SIPProvidersPersistor,
                        m_sipSorceryPersistor.SIPProviderBindingsPersistor);

                    m_sipRegAgentDaemon.Start();
                }

                #region Initialise the SIP Application Server and its logging mechanisms including CDRs.

                logger.Debug("Initiating SIP Application Server Agent.");

                // Send events from this process to the monitoring socket.
                if (m_monitorEventLoopbackPort != 0) {
                    m_monitorEventWriter = new SIPMonitorEventWriter(m_monitorEventLoopbackPort);
                }

                if (m_sipSorceryPersistor != null && m_sipSorceryPersistor.SIPCDRPersistor != null) {
                    SIPCDR.NewCDR += m_sipSorceryPersistor.QueueCDR;
                    SIPCDR.HungupCDR += m_sipSorceryPersistor.QueueCDR;
                    SIPCDR.CancelledCDR += m_sipSorceryPersistor.QueueCDR;
                }

                #region Initialise the SIPTransport layers.

                m_sipTransport = new SIPTransport(SIPDNSManager.Resolve, new SIPTransactionEngine(), true);
                if (m_appServerEndPoint != null) {
                    if (m_appServerEndPoint.SIPProtocol == SIPProtocolsEnum.udp) {
                        logger.Debug("Using single SIP transport socket for App Server " + m_appServerEndPoint.ToString() + ".");
                        m_sipTransport.AddSIPChannel(new SIPUDPChannel(m_appServerEndPoint.SocketEndPoint));
                    }
                    else if (m_appServerEndPoint.SIPProtocol == SIPProtocolsEnum.tcp) {
                        logger.Debug("Using single SIP transport socket for App Server " + m_appServerEndPoint.ToString() + ".");
                        m_sipTransport.AddSIPChannel(new SIPTCPChannel(m_appServerEndPoint.SocketEndPoint));
                    }
                    else {
                        throw new ApplicationException("The SIP End Point of " + m_appServerEndPoint + " cannot be used with the App Server transport layer.");
                    }
                }
                else if (m_sipAppServerSocketsNode != null) {
                    m_sipTransport.AddSIPChannel(SIPTransportConfig.ParseSIPChannelsNode(m_sipAppServerSocketsNode));
                }
                else {
                    throw new ApplicationException("The SIP App Server could not be started, no SIP sockets have been configured.");
                }

                m_sipTransport.SIPRequestInTraceEvent += LogSIPRequestIn;
                m_sipTransport.SIPRequestOutTraceEvent += LogSIPRequestOut;
                m_sipTransport.SIPResponseInTraceEvent += LogSIPResponseIn;
                m_sipTransport.SIPResponseOutTraceEvent += LogSIPResponseOut;
                m_sipTransport.SIPBadRequestInTraceEvent += LogSIPBadRequestIn;
                m_sipTransport.SIPBadResponseInTraceEvent += LogSIPBadResponseIn;
                m_sipTransport.UnrecognisedMessageReceived += UnrecognisedMessageReceived;

                #endregion

                m_dialPlanEngine = new DialPlanEngine(
                    m_sipTransport,
                    m_sipSorceryPersistor.SIPDomainManager.GetDomain,
                    FireSIPMonitorEvent,
                    m_sipSorceryPersistor.SIPAccountsPersistor,
                    m_sipSorceryPersistor.SIPRegistrarBindingPersistor.Get,
                    m_sipSorceryPersistor.SIPDialPlanPersistor,
                    m_customerSessionManager.CustomerPersistor,
                    m_outboundProxy,
                    m_rubyScriptCommonPath);

                m_callManager = new SIPCallManager(
                     m_sipTransport,
                     m_outboundProxy,
                     FireSIPMonitorEvent,
                     m_sipSorceryPersistor.SIPDialoguePersistor,
                     m_sipSorceryPersistor.SIPCDRPersistor,
                     m_dialPlanEngine,
                     m_sipSorceryPersistor.SIPDialPlanPersistor.Get,
                     m_sipSorceryPersistor.SIPAccountsPersistor.Get,
                     m_sipSorceryPersistor.SIPRegistrarBindingPersistor.Get,
                     m_sipSorceryPersistor.SIPProvidersPersistor.Get,
                     m_sipSorceryPersistor.SIPDomainManager.GetDomain,
                     m_customerSessionManager.CustomerPersistor,
                     m_traceDirectory);
                m_callManager.Start();

                if (m_sipCallDispatcherWorkersNode != null && !m_sipCallDispatcherScriptPath.IsNullOrBlank()) {
                    m_callDispatcher = new SIPCallDispatcher(
                        FireSIPMonitorEvent,
                        m_sipTransport,
                        m_sipCallDispatcherWorkersNode,
                        m_outboundProxy,
                        m_sipCallDispatcherScriptPath);
                }

                m_notifyManager = new SIPNotifyManager(
                    m_sipTransport,
                    m_outboundProxy,
                    FireSIPMonitorEvent,
                    m_sipSorceryPersistor.SIPAccountsPersistor.Get,
                    m_sipSorceryPersistor.SIPRegistrarBindingPersistor.Get,
                    m_sipSorceryPersistor.SIPDomainManager.GetDomain);
                m_notifyManager.Start();

                m_appServerCore = new SIPAppServerCore(
                    m_sipTransport,
                    m_sipSorceryPersistor.SIPDomainManager.GetDomain,
                    m_sipSorceryPersistor.SIPAccountsPersistor.Get,
                    FireSIPMonitorEvent,
                    m_callManager,
                    m_callDispatcher,
                    m_notifyManager,
                    SIPRequestAuthenticator.AuthenticateSIPRequest,
                    m_outboundProxy);

                #endregion

                try {
                    if (m_sipSorceryPersistor == null) {
                        logger.Warn("Provisioning hosted service could not be started as Persistor object was null.");
                    }
                    else {
                        ProvisioningServiceInstanceProvider instanceProvider = new ProvisioningServiceInstanceProvider(
                            m_sipSorceryPersistor.SIPAccountsPersistor,
                            m_sipSorceryPersistor.SIPDialPlanPersistor,
                            m_sipSorceryPersistor.SIPProvidersPersistor,
                            m_sipSorceryPersistor.SIPProviderBindingsPersistor,
                            m_sipSorceryPersistor.SIPRegistrarBindingPersistor,
                            m_sipSorceryPersistor.SIPDialoguePersistor,
                            m_sipSorceryPersistor.SIPCDRPersistor,
                            m_customerSessionManager,
                            m_sipSorceryPersistor.SIPDomainManager,
                            FireSIPMonitorEvent,
                            0);

                        m_sipProvisioningHost = new ServiceHost(typeof(SIPProvisioningWebService));
                        m_sipProvisioningHost.Description.Behaviors.Add(instanceProvider);
                        m_sipProvisioningHost.Open();

                        if (m_sipRegAgentDaemon == null) {
                            m_sipRegAgentDaemon = new SIPRegAgentDaemon(
                                m_sipSorceryPersistor.SIPProvidersPersistor,
                                m_sipSorceryPersistor.SIPProviderBindingsPersistor);

                            // DON'T START THE DAEMON. IN THIS CASE ONLY THE LINK BETWEEN THE PROVIDER AND BINDINGS PERSISTORS IS NEEDED.
                            //m_sipRegAgentDaemon.Start();
                        }

                        logger.Debug("Provisioning hosted service successfully started on " + m_sipProvisioningHost.BaseAddresses[0].AbsoluteUri + ".");
                    }
                }
                catch (Exception excp) {
                    logger.Warn("Exception starting Provisioning hosted service. " + excp.Message);
                }

                try {
                    m_accessPolicyHost = new ServiceHost(typeof(CrossDomainService));
                    m_accessPolicyHost.Open();

                    logger.Debug("CrossDomain hosted service successfully started on " + m_accessPolicyHost.BaseAddresses[0].AbsoluteUri + ".");
                }
                catch (Exception excp) {
                    logger.Warn("Exception starting CrossDomain hosted service. " + excp.Message);
                }

                try {
                    CallManagerServiceInstanceProvider callManagerSvcInstanceProvider = new CallManagerServiceInstanceProvider(m_callManager);

                    Uri callManagerBaseAddress = null;
                    if (m_callManagerServiceAddress != null) {
                        logger.Debug("Adding service address to Call Manager Service " + m_callManagerServiceAddress + ".");
                        callManagerBaseAddress = new Uri(m_callManagerServiceAddress);
                    }

                    if (callManagerBaseAddress != null) {
                        m_callManagerSvcHost = new ServiceHost(typeof(CallManagerServices), callManagerBaseAddress);
                    }
                    else {
                        m_callManagerSvcHost = new ServiceHost(typeof(CallManagerServices));
                    }

                    m_callManagerSvcHost.Description.Behaviors.Add(callManagerSvcInstanceProvider);

                    m_callManagerSvcHost.Open();

                    logger.Debug("CallManager hosted service successfully started on " + m_callManagerSvcHost.BaseAddresses[0].AbsoluteUri + ".");
                }
                catch (Exception excp) {
                    logger.Warn("Exception starting CallManager hosted service. " + excp.Message);
                }

                // Initialise random number to save delay on first SIP request.
                ThreadPool.QueueUserWorkItem(delegate { Crypto.GetRandomString(); });
            }
            catch (Exception excp) {
                logger.Error("Exception SIPAppServerDaemon Start. " + excp.Message);
                throw excp;
            }
        }

        public void Stop() {
            try {
                logger.Debug("SIP Application Server stopping...");

                DNSManager.Stop();
                m_dialPlanEngine.StopScriptMonitoring = true;

                if (m_accessPolicyHost != null) {
                    m_accessPolicyHost.Close();
                }

                if (m_sipProvisioningHost != null) {
                    m_sipProvisioningHost.Close();
                }

                if (m_callManagerSvcHost != null) {
                    m_callManagerSvcHost.Close();
                }

                if (m_callManager != null) {
                    m_callManager.Stop();
                }

                if (m_notifyManager != null) {
                    m_notifyManager.Stop();
                }

                if (m_monitorEventWriter != null) {
                    m_monitorEventWriter.Close();
                }

                if (m_sipProxyDaemon != null) {
                    m_sipProxyDaemon.Stop();
                }

                if (m_sipMonitorDaemon != null) {
                    m_sipMonitorDaemon.Stop();
                }

                if (m_sipRegistrarDaemon != null) {
                    m_sipRegistrarDaemon.Stop();
                }

                if (m_sipRegAgentDaemon != null) {
                    m_sipRegAgentDaemon.Stop();
                }

                // Shutdown the SIPTransport layer.
                m_sipTransport.Shutdown();

                m_sipSorceryPersistor.StopCDRWrites = true;

                logger.Debug("SIP Application Server stopped.");
            }
            catch (Exception excp) {
                logger.Error("Exception SIPAppServerDaemon Stop." + excp.Message);
            }
        }

        #region Logging functions.

        private void FireSIPMonitorEvent(SIPMonitorEvent sipMonitorEvent) {
            try {
                if (sipMonitorEvent != null) {
                    if (m_monitorEventWriter != null && sipMonitorEvent.EventType != SIPMonitorEventTypesEnum.SIPTransaction) {
                        m_monitorEventWriter.Send(sipMonitorEvent);
                    }

                    if (sipMonitorEvent.ServerType == SIPMonitorServerTypesEnum.RegisterAgent ||
                        (sipMonitorEvent.GetType() != typeof(SIPMonitorMachineEvent) &&
                        sipMonitorEvent.EventType != SIPMonitorEventTypesEnum.FullSIPTrace &&
                        sipMonitorEvent.EventType != SIPMonitorEventTypesEnum.SIPTransaction &&
                        sipMonitorEvent.EventType != SIPMonitorEventTypesEnum.Timing &&
                        sipMonitorEvent.EventType != SIPMonitorEventTypesEnum.ContactRegisterInProgress &&
                        sipMonitorEvent.EventType != SIPMonitorEventTypesEnum.Monitor &&
                        sipMonitorEvent.EventType != SIPMonitorEventTypesEnum.UnrecognisedMessage)) {
                        //logger.Debug("as (" + DateTime.Now.ToString("mm:ss:fff") + " " + sipMonitorEvent.Username + ") " + sipMonitorEvent.Message);
                        string eventUsername = (sipMonitorEvent.Username.IsNullOrBlank()) ? null : " " + sipMonitorEvent.Username;
                        dialPlanLogger.Debug("as (" + DateTime.Now.ToString("mm:ss:fff") + eventUsername + "): " + sipMonitorEvent.Message);
                    }
                }
            }
            catch (Exception excp) {
                logger.Error("Exception FireSIPMonitorEvent. " + excp.Message);
            }
        }

        private void LogSIPRequestIn(SIPEndPoint localSIPEndPoint, SIPEndPoint endPoint, SIPRequest sipRequest) {
            string message = "App Svr Received: " + localSIPEndPoint.ToString() + "<-" + endPoint.ToString() + "\r\n" + sipRequest.ToString();
            //logger.Debug("as: request in " + sipRequest.Method + " " + localSIPEndPoint.ToString() + "<-" + endPoint.ToString() + ", callid=" + sipRequest.Header.CallId + ".");
            FireSIPMonitorEvent(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.FullSIPTrace, message, sipRequest.Header.From.FromURI.User, localSIPEndPoint, endPoint));
        }

        private void LogSIPRequestOut(SIPEndPoint localSIPEndPoint, SIPEndPoint endPoint, SIPRequest sipRequest) {
            string message = "App Svr Sent: " + localSIPEndPoint.ToString() + "->" + endPoint.ToString() + "\r\n" + sipRequest.ToString();
            //logger.Debug("as: request out " + sipRequest.Method + " " + localSIPEndPoint.ToString() + "->" + endPoint.ToString() + ", callid=" + sipRequest.Header.CallId + ".");
            FireSIPMonitorEvent(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.FullSIPTrace, message, sipRequest.Header.From.FromURI.User, localSIPEndPoint, endPoint));
        }

        private void LogSIPResponseIn(SIPEndPoint localSIPEndPoint, SIPEndPoint endPoint, SIPResponse sipResponse) {
            string message = "App Svr Received: " + localSIPEndPoint.ToString() + "<-" + endPoint.ToString() + "\r\n" + sipResponse.ToString();
            //logger.Debug("as: response in " + sipResponse.Header.CSeqMethod + " " + localSIPEndPoint.ToString() + "<-" + endPoint.ToString() + ", callid=" + sipResponse.Header.CallId + ".");
            FireSIPMonitorEvent(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.FullSIPTrace, message, sipResponse.Header.From.FromURI.User, localSIPEndPoint, endPoint));
        }

        private void LogSIPResponseOut(SIPEndPoint localSIPEndPoint, SIPEndPoint endPoint, SIPResponse sipResponse) {
            string message = "App Svr Sent: " + localSIPEndPoint.ToString() + "->" + endPoint.ToString() + "\r\n" + sipResponse.ToString();
            //logger.Debug("as: response out " + sipResponse.Header.CSeqMethod + " " + localSIPEndPoint.ToString() + "->" + endPoint.ToString() + ", callid=" + sipResponse.Header.CallId + ".");
            FireSIPMonitorEvent(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.FullSIPTrace, message, sipResponse.Header.From.FromURI.User, localSIPEndPoint, endPoint));
        }

        private void LogSIPBadRequestIn(SIPEndPoint localSIPEndPoint, SIPEndPoint endPoint, string sipMessage, SIPValidationFieldsEnum errorField) {
            string message = "App Svr Bad Request Received: " + localSIPEndPoint + "<-" + endPoint.ToString() + ", " + errorField;
            string fullMessage = "App Svr Bad Request Received: " + localSIPEndPoint + "<-" + endPoint.ToString() + ", " + errorField + "\r\n" + sipMessage;
            LogSIPBadMessage(message, fullMessage);
        }

        private void LogSIPBadResponseIn(SIPEndPoint localSIPEndPoint, SIPEndPoint endPoint, string sipMessage, SIPValidationFieldsEnum errorField) {
            string message = "App Svr Bad Response Received: " + localSIPEndPoint + "<-" + endPoint.ToString() + ", " + errorField;
            string fullMessage = "App Svr Bad Response Received: " + localSIPEndPoint + "<-" + endPoint.ToString() + ", " + errorField + "\r\n" + sipMessage;
            LogSIPBadMessage(message, fullMessage);
        }

        private void LogSIPBadMessage(string message, string fullTraceMessage) {
            FireSIPMonitorEvent(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.BadSIPMessage, message, null));
            FireSIPMonitorEvent(new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.FullSIPTrace, fullTraceMessage, null));
        }

        private void UnrecognisedMessageReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint fromEndPoint, byte[] buffer) {
            int bufferLength = (buffer != null) ? buffer.Length : 0;
            string msg = null;

            if (bufferLength > 0) {
                if (buffer.Length > 128) {
                    msg = " =>" + Encoding.ASCII.GetString(buffer, 0, 128) + "...<=";
                }
                else {
                    msg = " =>" + Encoding.ASCII.GetString(buffer) + "<=";
                }
            }

            SIPMonitorEvent unrecgMsgEvent = new SIPMonitorControlClientEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.UnrecognisedMessage, "Unrecognised packet received from " + fromEndPoint.ToString() + " on " + localSIPEndPoint.ToString() + ", bytes=" + bufferLength + " " + msg + ".", null);
            FireSIPMonitorEvent(unrecgMsgEvent);
        }

        #endregion
    }
}
