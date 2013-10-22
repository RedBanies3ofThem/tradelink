using System;
using System.Collections.Generic;
using Blackwood.Framework;
using Blackwood.CBWMessages;
using TradeLink.API;
using TradeLink.Common;

namespace ServerBlackwood
{
    public delegate void BWConnectedEventHandler(object sender, bool BWConnected);
    public delegate void TLSendDelegate(string message, MessageTypes type, string client);

    public class ServerBlackwood
    {
        // broker members
        private BWSession m_Session;
        private System.ComponentModel.IContainer components;
        private uint bwHistReqID = 5000;
        public event BWConnectedEventHandler BWConnectedEvent;
        protected virtual void OnBWConnectedEvent(bool BWConnected) { BWConnectedEvent(this, BWConnected); }

        // tradelink members
        public event DebugDelegate SendDebug;
        private bool _valid = false;
        public bool isValid { get { return _valid; } }
        PositionTracker pt = new PositionTracker();


        public ServerBlackwood(TLServer tls)
        {
            tl = tls;
            // broker stuff
            m_Session = new BWSession();
            m_Session.OnAccountMessage2 += new BWSession.AccountMessageHandler2(m_Session_OnAccountMessage);
            m_Session.OnHistMessage2 += new BWSession.HistoricMessageHandler2(m_Session_OnHistMessage);
            m_Session.OnTimeMessage2 += new BWSession.TimeMessageHandler2(m_Session_OnTimeMessage);
            m_Session.OnOrderMessage += new BWSession.OrderMessageHandler(m_Session_OnOrderMessage);
            m_Session.OnPositionMessage2 += new BWSession.PositionMessageHandler2(m_Session_OnPositionMessage);
            m_Session.OnExecutionMessage2 += new BWSession.ExecutionMessageHandler2(m_Session_OnExecutionMessage);
            m_Session.OnCancelMessage2 += new BWSession.CancelMessageHandler2(m_Session_OnCancelMessage);
            m_Session.OnRejectMessage2 += new BWSession.RejectMessageHandler2(m_Session_OnRejectMessage);

            // tradelink stuff
            tl.newProviderName = Providers.Blackwood;
            tl.newAcctRequest += new StringDelegate(ServerBlackwood_newAccountRequest);
            tl.newUnknownRequest += new UnknownMessageDelegate(ServerBlackwood_newUnknownRequest);
            tl.newFeatureRequest += new MessageArrayDelegate(ServerBlackwood_newFeatureRequest);
            tl.newOrderCancelRequest += new LongDelegate(ServerBlackwood_newOrderCancelRequest);
            tl.newSendOrderRequest += new OrderDelegateStatus(ServerBlackwood_newSendOrderRequest);
            tl.newRegisterSymbols += new SymbolRegisterDel(tl_newRegisterSymbols);
            tl.newPosList += new PositionAccountArrayDelegate(ServerBlackwood_newPosList);
            //DOMRequest += new IntDelegate(ServerBlackwood_DOMRequest);
        }


        bool _noverb = true;
        public bool VerbuseDebugging { get { return !_noverb; } set { _noverb = !value; tl.VerboseDebugging = VerbuseDebugging; } }
        void v(string msg)
        {
            if (_noverb)
                return;
            debug(msg);
        }


        List<BWStock> _stocks = new List<BWStock>();
        List<string> _symstk = new List<string>();
        void tl_newRegisterSymbols(string client, string symbols)
        {
            Basket b = BasketImpl.FromString(symbols);
            foreach (Security s in b)
            {
                // make sure we don't already have a subscription for this
                if (_symstk.Contains(s.symbol)) continue;
                BWStock stk = m_Session.GetStock(s.symbol);
                stk.Subscribe();
                stk.OnTrade2 += new BWStock.TradeHandler2(stk_OnTrade);
                stk.OnLevel1Update2 += new BWStock.Level1UpdateHandler2(stk_OnLevel1Update);
                _stocks.Add(stk);
                _symstk.Add(s.symbol);
                debug(String.Format("Subscribing...{0}", s.symbol));
            }
            // get existing list
            Basket list = tl.AllClientBasket;
            // remove old subscriptions
            for (int i = 0; i < _symstk.Count; i++)
            {

                if (!list.ToString().Contains(_symstk[i]) && (_stocks[i] != null))
                {
                    debug(String.Format("Unsubscribing...{0}", _symstk[i]));
                    try
                    {
                        _stocks[i].Unsubscribe();
                        _stocks[i] = null;
                        _symstk[i] = string.Empty;
                    }
                    catch { }
                }
            }
        }


        Position[] ServerBlackwood_newPosList(string account)
        {
            v(String.Format("Received position list request for account: {0}", account));
            foreach (BWStock s in m_Session.GetOpenPositions())
            {
                Position p = new PositionImpl(s.Symbol, (decimal)s.Price, s.Size, (decimal)s.ClosedPNL);
                pt.Adjust(p);
                v(String.Format("{0} found position: {1}", p.symbol, p.ToString()));
            }
            return pt.ToArray();
        }


        void ServerBlackwood_newImbalanceRequest()
        {
            v("received imbalance request.");
            m_Session.RequestNYSEImbalances();
        }


        bool isunique(Order o)
        {
            bool ret = !_longint.ContainsKey(o.id);
            return ret;
        }


        IdTracker _id = new IdTracker();
        long ServerBlackwood_newSendOrderRequest(Order o)
        {
            v(String.Format("{0} received sendorder request for: {1}", o.symbol, o.ToString()));
            if ((o.id != 0) && !isunique(o))
            {
                v(String.Format("{0} dropping duplicate order: {1}", o.symbol, o.ToString()));
                return (long)MessageTypes.DUPLICATE_ORDERID;
            }
            if (o.id == 0)
            {
                o.id = _id.AssignId;
                v(String.Format("No order id for {0}...assigning [{1}]", o.symbol, o.id));
            }

            string sSymbol = o.symbol;
            ORDER_SIDE orderSide = (o.side ? ORDER_SIDE.BUY : ORDER_SIDE.SELL);
            FEED_ID orderVenue = getVenueFromBW(o); // need to add pegged order types
            ORDER_TYPE orderType = (o.isStop ? (o.isLimit ? ORDER_TYPE.STOP_LIMIT : ORDER_TYPE.STOP_MARKET) : (o.isLimit ? ORDER_TYPE.LIMIT : ORDER_TYPE.MARKET));
            int orderTIF = (int)getDurationFromBW(o);
            uint orderSize = (uint)o.UnsignedSize;
            int orderReserve = o.UnsignedSize;
            double orderPrice = System.Convert.ToDouble(o.price);
            double orderStopPrice = System.Convert.ToDouble(o.stopp);
            // create a new BWOrder with these parameters
            BWOrder bwOrder = new BWOrder(m_Session, sSymbol, orderSide, orderSize, orderPrice, orderType, orderTIF, orderVenue, false, orderSize);

            SENDORDERUPDATE(bwOrder, o);

            // check market connection
            try
            {
                // GetStock throws an exception if not connected to Market Data
                BWStock stock = m_Session.GetStock(bwOrder.Symbol);
            }
            catch (ClientPortalConnectionException e)
            {
                debug(e.Message);
            }
            // send the order
            bwOrder.Send();
            debug(String.Format("{0} sent order @ ${1}", bwOrder.Symbol, orderPrice));
            return (long)MessageTypes.OK;
        }


        void ServerBlackwood_newOrderCancelRequest(long tlID)
        {
            v(String.Format("GotCancelRequest[{0}]", tlID));

            int bwID = 0;
            bool match = false;
            if (_longint.TryGetValue(tlID, out bwID))
            {
                if (bwID != 0)
                {
                    foreach (BWOrder o in m_Session.GetOpenOrders())
                    {
                        if (o.SmartID == bwID)
                        {
                            match = true;
                            if (!bw_cancelids.ContainsKey(bwID))
                            {
                                bw_cancelids.Add(bwID, false);
                                bool _isBWsent = false;

                                //validate bw
                                if (bw_cancelids.TryGetValue(bwID, out _isBWsent))
                                {
                                    if (_isBWsent)
                                    {
                                        v(String.Format("ServerBlackwood_newOrderCancelRequest. BW already sent cancel to TL KEY:[{0}]", bwID));
                                    }
                                    else
                                    {
                                        v(String.Format("ServerBlackwood_newOrderCancelRequest. BW ++ADDING++ to BW_CANCELIDs BW KEY:[{0}]", bwID));
                                        o.Cancel();
                                        debug(String.Format("...found TL[{0}] and canceled BW[{1}]", tlID, o.SmartID));
                                        bw_cancelids[bwID] = true;
                                        if (!tl_canceledids.ContainsKey(tlID))
                                            tl_canceledids.Add(tlID, false);
                                    }
                                }
                                else
                                {
                                    v(String.Format("ServerBlackwood_newOrderCancelRequest.bw_cancelids could not find KEY:[{0}]", bwID));
                                }
                            }
                            else
                            {
                                v(String.Format("already been here...bw_cancelid - ServerBlackwood_newOrderCancel KEY:[{0}]", bwID));
                                //validate bw
                                bool _isBWsent = false;
                                if (bw_cancelids.TryGetValue(bwID, out _isBWsent))
                                {
                                    if (_isBWsent)
                                    {
                                        v(String.Format("ServerBlackwood_newOrderCancelRequest. BW already sent cancel to TL KEY:[{0}]", bwID));
                                    }
                                    else
                                    {
                                        v(String.Format("ServerBlackwood_newOrderCancelRequest. BW ++ADDING++ to BW_CANCELIDs BW KEY:[{0}]", bwID));
                                        o.Cancel();
                                        debug(String.Format("...found TL[{0}] and canceled BW[{1}]", tlID, o.SmartID));
                                        bw_cancelids[bwID] = true;
                                        if (!tl_canceledids.ContainsKey(tlID))
                                            tl_canceledids.Add(tlID, false);
                                    }
                                }
                                else
                                {
                                    v(String.Format("ServerBlackwood_newOrderCancelRequest.bw_cancelids could not find KEY:[{0}]", bwID));
                                }
                            }
                        }
                    }
                }
                else
                {
                    v(String.Format("ERROR: BW[{0}] is ZERO!", bwID));
                }
            }
            if (!match) v(String.Format("Missing order...[{0}]", bwID));
        }


        MessageTypes[] ServerBlackwood_newFeatureRequest()
        {
            v("received feature request.");
            List<MessageTypes> f = new List<MessageTypes>();
            f.Add(MessageTypes.LIVEDATA);
            f.Add(MessageTypes.LIVETRADING);
            f.Add(MessageTypes.SIMTRADING);
            f.Add(MessageTypes.SENDORDER);
            f.Add(MessageTypes.ORDERCANCELREQUEST);
            f.Add(MessageTypes.ORDERCANCELRESPONSE);
            f.Add(MessageTypes.OK);
            f.Add(MessageTypes.BROKERNAME);
            f.Add(MessageTypes.CLEARCLIENT);
            f.Add(MessageTypes.CLEARSTOCKS);
            f.Add(MessageTypes.FEATUREREQUEST);
            f.Add(MessageTypes.FEATURERESPONSE);
            f.Add(MessageTypes.ORDERNOTIFY);
            f.Add(MessageTypes.REGISTERCLIENT);
            f.Add(MessageTypes.REGISTERSTOCK);
            f.Add(MessageTypes.SENDORDER);
            f.Add(MessageTypes.TICKNOTIFY);
            f.Add(MessageTypes.VERSION);
            f.Add(MessageTypes.IMBALANCEREQUEST);
            //f.Add(MessageTypes.IMBALANCERESPONSE);
            f.Add(MessageTypes.POSITIONREQUEST);
            f.Add(MessageTypes.POSITIONRESPONSE);
            f.Add(MessageTypes.ACCOUNTREQUEST);
            f.Add(MessageTypes.ACCOUNTRESPONSE);
            f.Add(MessageTypes.SENDORDERSTOP);
            f.Add(MessageTypes.SENDORDERMARKET);
            f.Add(MessageTypes.SENDORDERLIMIT);
            f.Add(MessageTypes.EXECUTENOTIFY);
            f.Add(MessageTypes.BARREQUEST);
            f.Add(MessageTypes.BARRESPONSE);
            return f.ToArray();
        }


        long ServerBlackwood_newUnknownRequest(MessageTypes t, string msg)
        {
            int _depth = 0;
            MessageTypes ret = MessageTypes.UNKNOWN_MESSAGE;
            switch (t)
            {
                case MessageTypes.DOMREQUEST:

                    _depth = Convert.ToInt32(msg);
                    v(String.Format("DOM received request for depth: {0}", _depth));
                    ret = MessageTypes.OK;
                    break;
                case MessageTypes.ISSHORTABLE:
                    return (long)(m_Session.GetStock(msg).IsHardToBorrow() ? 0 : 1);
                case MessageTypes.BARREQUEST:
                    v("received bar request: " + msg);
                    string[] r = msg.Split(',');
                    DBARTYPE barType = getBarTypeFromBW(r[(int)BarRequestField.BarInt]);
                    int tlDateS = int.Parse(r[(int)BarRequestField.StartDate]);
                    int tlTimeS = int.Parse(r[(int)BarRequestField.StartTime]);
                    DateTime dtStart = TradeLink.Common.Util.ToDateTime(tlDateS, tlTimeS);
                    int tlDateE = int.Parse(r[(int)BarRequestField.StartDate]);
                    int tlTimeE = int.Parse(r[(int)BarRequestField.StartTime]);
                    DateTime dtEnd = TradeLink.Common.Util.ToDateTime(tlDateE, tlTimeE);
                    uint custInt = 1;
                    if (!uint.TryParse(r[(int)BarRequestField.CustomInterval], out custInt))
                    {
                        custInt = 1;
                    }

                    m_Session.RequestHistoricData(r[(int)BarRequestField.Symbol], barType, dtStart, dtEnd, custInt, ++bwHistReqID);
                    ret = MessageTypes.OK;
                    break;
            }
            return (long)ret;
        }


        string ServerBlackwood_newAccountRequest()
        {
            return _acct;
        }


        int date = TradeLink.Common.Util.ToTLDate(); int time = 0;
        void m_Session_OnTimeMessage(object sender, CBWMsgTime timeMsg)
        {
            date = TradeLink.Common.Util.ToTLDate(timeMsg.Time.Value);
            time = TradeLink.Common.Util.ToTLTime(timeMsg.Time.Value);
        }


        void stk_OnLevel1Update(object sender, CBWMsgLevel1 quote)
        {
            Tick k = new TickImpl(quote.Symbol.Value);
            k.depth = 0;
            k.bid = System.Convert.ToDecimal(quote.Bid.Value);
            k.BidSize = quote.BidSize.Value;
            k.ask = System.Convert.ToDecimal(quote.Ask.Value);
            k.os = quote.AskSize.Value;
            k.date = date;
            k.time = TradeLink.Common.Util.ToTLTime();
            tl.newTick(k);
        }


        void stk_OnLevel2Update(object sender, CBWMsgLevel2 quote)
        {
            Tick k = new TickImpl(quote.Symbol);
            k.depth = quote.EcnOrder;
            k.bid = System.Convert.ToDecimal(quote.Bid.Value);
            k.BidSize = quote.BidSize.Value;
            k.be = quote.MarketMaker;
            k.ask = System.Convert.ToDecimal(quote.Ask.Value);
            k.os = quote.AskSize.Value;
            k.oe = quote.MarketMaker;
            k.date = date;
            k.time = TradeLink.Common.Util.ToTLTime();
            tl.newTick(k);
        }


        void stk_OnTrade(object sender, CBWMsgTrade print)
        {
            Tick k = new TickImpl(print.Symbol.Value);
            k.trade = System.Convert.ToDecimal(print.Price.Value);
            k.size = print.TradeSize.Value;
            k.ex = print.MarketMaker.Value;
            k.date = date;
            k.time = TradeLink.Common.Util.ToTLTime();
            tl.newTick(k);
        }


        TLServer tl;
        public void Start()
        {
            if (tl != null)
                tl.Start();
        }


        void m_Session_OnOrderMessage(object sender, BWOrder bwo)
        {
            //v(String.Format("ORDER.MSG.{0}.{1}: for BW[{2}]", bwo.Status.ToString(), bwo.Symbol, bwo.SmartID));

            switch (bwo.Status)
            {
                case STATUS.SERVER:
                    STATUSSERVERUPDATE(bwo);
                    break;
                case STATUS.MARKET:
                    STATUSMARKETUPDATE(bwo);
                    break;
                case STATUS.REJECT:
                    debug(String.Format("{0} order was rejected [{1}]", bwo.Symbol, bwo.SmartID));
                    break;
                case STATUS.DONE:
                    if (bwo.SizeRemaining == 0)
                        v(String.Format("STATUS.DONE.{0}: for BW[{1}]", bwo.Symbol, bwo.SmartID));
                    break;
            }
        }


        double _cpl = 0;
        string _acct = string.Empty;
        void m_Session_OnAccountMessage(object sender, CBWMsgAccount accountMsg)
        {
            string str = m_Session.Account;
            string[] strArr = str.Split('~');
            _acct = strArr[0];
            _cpl = accountMsg.ClosedProfit;
        }

        Dictionary<int, int> _exeID = new Dictionary<int, int>();
        void m_Session_OnExecutionMessage(object sender, CBWMsgExecution executionMsg)
        {
            if (!_exeID.ContainsKey(executionMsg.ExecutionID.Value))
            {
                _exeID.Add(executionMsg.ExecutionID.Value, executionMsg.SmartID.Value);
                foreach (KeyValuePair<long, int> ordID in _longint)
                    if (ordID.Value == executionMsg.SmartID.Value)
                    {
                        Trade t = new TradeImpl(executionMsg.Symbol.Value, System.Convert.ToDecimal(executionMsg.Price.Value), executionMsg.ExecSize.Value);
                        t.side = (executionMsg.Side == ORDER_SIDE.COVER) || (executionMsg.Side == ORDER_SIDE.BUY);
                        t.xtime = TradeLink.Common.Util.DT2FT(executionMsg.ExecutionTime.Value);
                        t.xdate = TradeLink.Common.Util.ToTLDate(executionMsg.ExecutionTime.Value);
                        t.Account = _acct;
                        t.id = ordID.Key;
                        t.ex = executionMsg.MarketMaker.Value;
                        tl.newFill(t);

                        v(String.Format("GotFill({0}) {1} @ ${2}", executionMsg.Symbol.Value, executionMsg.ExecSize.Value, executionMsg.Price.Value));
                        if (t.isFilled)
                        {
                            debug(String.Format("{0} has been filled {1} shares at ${2} TL[{3}]", t.symbol, t.xsize, t.xprice, t.id));
                        }
                    }
            }
        }


        Dictionary<int, int> _cancelMsgID = new Dictionary<int, int>();
        void m_Session_OnCancelMessage(object sender, CBWMsgCancel cancelMsg)
        {
            CANCELUPDATE(cancelMsg);
        }


        void m_Session_OnPositionMessage(object sender, CBWMsgPosition positionMsg)
        {
            string sym = positionMsg.Symbol.Value;
            int size = positionMsg.PosSize.Value;
            decimal price = System.Convert.ToDecimal(positionMsg.Price.Value);
            decimal cpl = System.Convert.ToDecimal(positionMsg.CloseProfit.Value);
            Position p = new PositionImpl(sym, price, size, cpl, _acct);
            pt.NewPosition(p);
        }


        public bool Start(string user, string pw, string ipaddress, int data2)
        {
            v("Start request:  ServerBlackwood");
            System.Net.IPAddress bwIP = System.Net.IPAddress.Parse(ipaddress);
            m_Session.OnMarketDataClientPortalConnectionChange += new BWSession.ClientPortalConnectionChangeHandler(OnMarketConnectionChange);

            try
            {
                m_Session.ConnectToOrderRouting(user, pw, bwIP, Properties.Settings.Default.orderport, true, true, true, true);
                m_Session.ConnectToHistoricData(user, pw, bwIP, Properties.Settings.Default.historicalport);
                m_Session.ConnectToMarketData(user, pw, bwIP, Properties.Settings.Default.dataport, true);
            }
            catch (Blackwood.Framework.ClientPortalConnectionException)
            {
                debug("error: Unable to connect to market data client portal.");
                _valid = false;
                return _valid;
            }
            _valid = true;
            return _valid;
        }


        public void Stop()
        {
            try
            {
                v("got stop request on blackwood connector.");
                m_Session.DisconnectFromOrders();
                m_Session.DisconnectFromHistoricData();
                m_Session.DisconnectFromMarketData();
                m_Session.CloseSession();
                m_Session.OnMarketDataClientPortalConnectionChange -= new BWSession.ClientPortalConnectionChangeHandler(OnMarketConnectionChange);
            }
            catch { }
        }


        private void OnMarketConnectionChange(object sender, bool Connected)
        {
            if (m_Session.IsConnectedToMarketData & m_Session.IsConnectedToOrderRouting)
                OnBWConnectedEvent(Connected);
            else OnBWConnectedEvent(false);

            debug("Connected market data: " + m_Session.IsConnectedToMarketData.ToString());
            debug("Connected order port:    " + m_Session.IsConnectedToOrderRouting.ToString());
            debug("Connected history port:  " + m_Session.IsConnectedToHistoricData.ToString());
        }


        private void m_Session_OnHistMessage(object sender, CBWMsgHistResponse histMsg)
        {
            if (histMsg.Error.Value.Length > 0) debug("ERROR: " + histMsg.Error);
            else
            {
                v(String.Format("{0} received bar history data containing {1} bars.", histMsg.Symbol.Value, histMsg.Bars.Length));
                if (histMsg.Bars != null && histMsg.Bars.Length > 0)
                {
                    string sym = histMsg.Symbol;
                    foreach (CBWMsgHistResponse.BarData bar in histMsg.Bars)
                    {
                        int tlDate = TradeLink.Common.Util.ToTLDate(bar.Time);
                        int tlTime = TradeLink.Common.Util.ToTLTime(bar.Time);
                        Bar tlBar = new BarImpl((decimal)bar.Open, (decimal)bar.High, (decimal)bar.Low, (decimal)bar.Close, (long)bar.Volume, tlDate, tlTime, sym, Convert.ToInt32(histMsg.Interval));
                        for (int i = 0; i < tl.NumClients; i++)
                            tl.TLSend(BarImpl.Serialize(tlBar), MessageTypes.BARRESPONSE, i.ToString());
                    }
                }
            }
        }

        #region BWFrontEnd
        private BWTIF getDurationFromBW(Order o)
        {
            BWTIF bwTIF;
            string strTIF = o.TIF;
            switch (strTIF)
            {
                case "DAY":
                    bwTIF = BWTIF.DAY;
                    break;
                case "IOC":
                    bwTIF = BWTIF.IOC;
                    break;
                case "FOK":
                    bwTIF = BWTIF.FOK;
                    break;
                case "CLO":
                    bwTIF = BWTIF.CLO;
                    break;
                case "OPG":
                    bwTIF = BWTIF.OPG;
                    break;
                default:
                    bwTIF = BWTIF.DAY;
                    break;
            }
            return bwTIF;
        }


        private FEED_ID getVenueFromBW(Order o)
        {
            FEED_ID bwVenue;
            string strFeed = o.ex;
            switch (strFeed)
            {
                case "ARCA":
                    bwVenue = FEED_ID.ARCA;
                    break;
                case "BATS":
                    bwVenue = FEED_ID.BATS;
                    break;
                case "INET":
                    bwVenue = FEED_ID.INET;
                    break;
                case "NSDQ":
                    bwVenue = FEED_ID.NASDAQ;
                    break;
                case "NYSE":
                    bwVenue = FEED_ID.BLZ;
                    break;
                case "NITE":
                    bwVenue = FEED_ID.NITE;
                    break;
                case "EDGA":
                    bwVenue = FEED_ID.EDGA;
                    break;
                case "EDGX":
                    bwVenue = FEED_ID.EDGX;
                    break;
                case "CSFB":
                    bwVenue = FEED_ID.CSFB;
                    break;
                case "Toronto":
                    bwVenue = FEED_ID.TSE;
                    break;
                case "Vancouver":
                    bwVenue = FEED_ID.VSE;
                    break;
                case "Citibank":
                    bwVenue = FEED_ID.ATDPING;
                    break;
                case "Goldman":
                    bwVenue = FEED_ID.GSCO;
                    break;
                case "MAXM":
                    bwVenue = FEED_ID.MAXM;
                    break;
                case "CME":
                    bwVenue = FEED_ID.CME;
                    break;
                default:
                    bwVenue = FEED_ID.NONE;
                    break;
            }
            return bwVenue;
        }


        private DBARTYPE getBarTypeFromBW(string str)
        {
            DBARTYPE bwType;
            switch (str)
            {
                case "DAILY":
                    bwType = DBARTYPE.DAILY;
                    break;
                case "WEEKLY":
                    bwType = DBARTYPE.WEEKLY;
                    break;
                case "MONTHLY":
                    bwType = DBARTYPE.MONTHLY;
                    break;
                case "TICK":
                    bwType = DBARTYPE.TICK;
                    break;
                case "INTRADAY":
                default:
                    bwType = DBARTYPE.INTRADAY;
                    break;
            }
            return bwType;
        }


        private ORDER_TYPE getorderType(Order o)
        {
            ORDER_TYPE bwOrderType = (o.isStop ?
                (o.isLimit ? ORDER_TYPE.STOP_LIMIT : ORDER_TYPE.STOP_MARKET) :
                (o.isLimit ? ORDER_TYPE.LIMIT : ORDER_TYPE.MARKET));
            if (o.ValidInstruct == OrderInstructionType.PEG2MID)
                bwOrderType = ORDER_TYPE.MID_PEGGED;
            if (o.ValidInstruct == OrderInstructionType.PEG2MKT)
                bwOrderType = ORDER_TYPE.MKT_PEGGED;
            return bwOrderType;
        }
        #endregion

        // Collective order maps
        Dictionary<long, int> _longint = new Dictionary<long, int>();
        Dictionary<int, long> _intlong = new Dictionary<int, long>();
        List<long> sentNewOrders = new List<long>();
        Dictionary<long, BWOrder> orderz = new Dictionary<long, BWOrder>();
        Dictionary<int, bool> bw_cancelids = new Dictionary<int, bool>();
        Dictionary<long, bool> tl_canceledids = new Dictionary<long, bool>();


        void debug(string msg)
        {
            if (SendDebug != null)
                SendDebug(msg);
        }


        private void SENDORDERUPDATE(BWOrder bwo, Order o)
        {
            long _tlid = o.id;
            int _bwid = bwo.ClientOrderID;
            // update order map
            if (_tlid != 0)
            {
                // TL 2 broker
                if (!_longint.ContainsKey(_tlid))
                {
                    //v(String.Format("Mapping TL:[{0}] to BW:[{1}]", _tlid, _bwid));
                    lock (_longint)
                    {
                        _longint.Add(_tlid, _bwid);
                    }
                }
                else
                {
                    // update the existing ID
                    v(String.Format("-----WARNING! Updating TL:[{0}] with BW:[{1}]", _tlid, _bwid));
                    lock (_longint)
                    {
                        _longint[_tlid] = _bwid;
                    }
                }
                // broker 2 TL
                if (!_intlong.ContainsKey(_bwid))
                {
                    //v(String.Format("Mapping BW:[{0}] to TL:[{1}]", _bwid, _tlid));
                    lock (_intlong)
                    {
                        _intlong.Add(_bwid, _tlid);
                    }
                }
                else
                {
                    // update the existing ID
                    v(String.Format("-----WARNING! Updating BW:[{0}] with TL:[{1}]", _bwid, _tlid));
                    lock (_intlong)
                    {
                        _intlong[_bwid] = _tlid; // this actually shouldn't be called...!
                    }
                }
            }
            else
            {
                v("WARNING! Incoming TL order does not have an id. It will be generated.");
            }
        }


        private void STATUSSERVERUPDATE(BWOrder bwo)
        {
            long _tlid = 0;
            int _bwid = bwo.ClientOrderID;
            int _smartID = bwo.SmartID;
            // rectify ClientOrderID to SmartID
            // check for ClientOrderID 'key'
            if (_intlong.ContainsKey(_bwid))
            {
                _intlong.TryGetValue(_bwid, out _tlid);
                if (_tlid != 0)
                {
                    // update TL with BWid
                    lock (_intlong)
                    {
                        _intlong.Remove(_bwid);
                        _intlong.Add(_smartID, _tlid);
                    }
                    lock (_longint)
                    {
                        _longint[_tlid] = _smartID;
                    }
                    //v(String.Format("RECTIFYING! Updating order map, TL:[{0}] with BW:[{1}]", _tlid, _smartID));
                }
                else
                {
                    v(String.Format("Order for {0} put TL [{0}] to ZERO ", bwo.Symbol, _tlid));
                }
            }
            else
            {
                if (_intlong.ContainsKey(_smartID))
                {
                    // v(String.Format("We have already updated BW and TL to reflect smartID [{0}]", _smartID));
                }
                else
                {
                    long _cancelID = _id.AssignId;
                    lock (_intlong)
                    {
                        _intlong.Add(_smartID, _cancelID);
                    }
                    lock (_longint)
                    {
                        _longint.Add(_cancelID, _smartID);
                    }
                    //orderz.Add(_cancelID, bwo);
                    debug(String.Format("{0} ***manual order ack***", bwo.Symbol));
                    //v(String.Format("+++Manual Order ack+++ SmartID:[{0}] getting tagged to *NEW* TL_ID: [{1}] and BWOrder: [{2}]", 
                    //    _smartID, _cancelID, bwo.ToString()));
                }
            }
        }


        private void STATUSMARKETUPDATE(BWOrder bwo)
        {
            long _tlid = 0;
            int _bwid = bwo.SmartID;

            if (_intlong.ContainsKey(_bwid))
            {
                // locate TL order via bwo.SmartID
                lock (_intlong)
                {
                    _intlong.TryGetValue(_bwid, out _tlid);
                }
            }
            else
            {
                v(String.Format("STATUS.MARKET.UPDATE.{0}: smartID [{1}] cannot be found...", bwo.Symbol, _bwid));
            }

            // create TL order
            Order o = new OrderImpl(bwo.Symbol, Convert.ToInt32(bwo.Size));
            o.id = _tlid;
            o.side = (bwo.OrderSide == ORDER_SIDE.BUY) || (bwo.OrderSide == ORDER_SIDE.COVER);
            o.price = System.Convert.ToDecimal(bwo.LimitPrice);
            o.stopp = System.Convert.ToDecimal(bwo.StopPrice);
            o.Account = _acct;
            o.ex = bwo.FeedID.ToString();

            // update new orders list
            if (!sentNewOrders.Contains(o.id))
            {
                tl.newOrder(o);
                sentNewOrders.Add(o.id);
                string _direction = o.side ? "+++++" : "-----";
                debug(String.Format("{0}>{1} sent for {2} @ ${3}   TL[{4}]", _direction, o.symbol, o.size, o.price, o.id));
            }
            else
            {
                //v(String.Format("STATUS.MARKET ...sentNewOrders already contains TL_ID: [{0}]", o.id));
            }

            // update orderz map
            if (!orderz.ContainsKey(o.id))
            {
                lock (orderz)
                {
                    orderz.Add(o.id, bwo);
                }
                //v(String.Format("STATUS.MARKET ...adding to orderz: TL_ID: [{0}] with bwo: [{1}]", o.id, bwo.ToString()));
            }
            else
            {
                //v(String.Format("STATUS.MARKET ...orderz already contains TL_ID: [{0}] with bwo: [{1}]", o.id, bwo.ToString()));
            }
        }


        private void CANCELUPDATE(CBWMsgCancel cancelMsg)
        {
            if (!_cancelMsgID.ContainsKey(cancelMsg.CancelID))
            {
                lock (_cancelMsgID)
                {
                    _cancelMsgID.Add(cancelMsg.CancelID.Value, cancelMsg.SmartID.Value);
                }
                double _price = Math.Round(cancelMsg.Price, 2);

                v(String.Format("ServerID: {0}, CancelID: {1}, CancelTime: {2}, CIOrdID: {3}, FeedID: {4}, OrderSize: {5}, OrderType: {6}, Price: {7}, OrderTime: {8}",
                    cancelMsg.ServerID.ToString(), cancelMsg.CancelID.ToString(), cancelMsg.CancelTime.ToString(), cancelMsg.ClOrdID.ToString(),
                    cancelMsg.FeedID.ToString(), cancelMsg.OrderSize.ToString(), cancelMsg.OrderType.ToString(), _price.ToString(),
                    cancelMsg.OrderTime.ToString()));
                int _smartID = cancelMsg.SmartID;
                long _tlid = 0;
                if (_intlong.ContainsKey(_smartID))
                {
                    if (_intlong.TryGetValue(_smartID, out _tlid))
                    {
                        if (!tl_canceledids.ContainsKey(_tlid))
                        {
                            lock (tl_canceledids)
                            {
                                tl_canceledids.Add(_tlid, false);
                            }

                            //validate TL
                            bool _isTLsent = false;
                            if (tl_canceledids.TryGetValue(_tlid, out _isTLsent))
                            {
                                if (_isTLsent)
                                {
                                    v(String.Format("CANCELUPDATE. TL already sent cancel to BW KEY:[{0}]", _smartID));
                                }
                                else
                                {
                                    v(String.Format("CANCELUPDATE. TL ++ADDING++ to TL_CANCELIDs BW KEY:[{0}]", _smartID));
                                    tl.newCancel(_tlid);
                                    lock (tl_canceledids)
                                    {
                                        tl_canceledids[_tlid] = true;
                                    }
                                    debug(String.Format("...found TL[{0}] and canceled BW[{1}]", _tlid, _smartID));
                                    if (!bw_cancelids.ContainsKey(_smartID))
                                    {
                                        lock (bw_cancelids)
                                        {
                                            bw_cancelids.Add(_smartID, false);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                v(String.Format("CANCELUPDATE.tl_canceledids could not find TL KEY:[{0}]", _tlid));
                            }
                        }
                        else
                        {
                            //validate TL
                            bool _isTLsent = false;
                            if (tl_canceledids.TryGetValue(_tlid, out _isTLsent))
                            {
                                if (_isTLsent)
                                {
                                    v(String.Format("CANCELUPDATE. TL already sent cancel to BW KEY:[{0}]", _smartID));
                                }
                                else
                                {
                                    v(String.Format("CANCELUPDATE. TL ++ADDING++ to TL_CANCELIDs BW KEY:[{0}]", _smartID));
                                    tl.newCancel(_tlid);
                                    lock (tl_canceledids)
                                    {
                                        tl_canceledids[_tlid] = true;
                                    }
                                    debug(String.Format("...found TL[{0}] and canceled BW[{1}]", _tlid, _smartID));
                                    if (!bw_cancelids.ContainsKey(_smartID))
                                    {
                                        lock (bw_cancelids)
                                        {
                                            bw_cancelids.Add(_smartID, false);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                v(String.Format("CANCELUPDATE.tl_canceledids could not find TL KEY:[{0}]", _tlid));
                            }
                        }
                    }
                    else
                    {
                        v(String.Format("Error in getting value for _intlong with SmartID[{0}]", _smartID));
                    }
                }
                else
                {
                    //v("appears to have already sent cancel for [" + _smartID + "]");
                }
            }
        }


        private void m_Session_OnRejectMessage(object sender, CBWMsgReject rejectMsg)
        {
            long _tlid = 0;

            int _bwid = rejectMsg.ClientOrderID;
            lock (_intlong)
            {
                _intlong.TryGetValue(_bwid, out _tlid);
            }
            v(String.Format("REJECT_TYPE.{0}.{1} for TL order [{2}]", rejectMsg.Type, rejectMsg.Symbol.Value, _tlid));
            switch (rejectMsg.RejectType.Value)
            {
                case REJECT_TYPE.REJECT_ORDER:
                    {
                        if (!tl_canceledids.ContainsKey(_tlid))
                        {
                            lock (tl_canceledids)
                            {
                                tl_canceledids.Add(_tlid, false);
                            }
                            bool _value = false;
                            if (tl_canceledids.TryGetValue(_tlid, out _value))
                            {
                                if (_value)
                                {
                                    v(String.Format("REJECT_ORDER. tl_canceledids has already sent cancel :[{0}]", _tlid));
                                }
                                else
                                {
                                    v(String.Format("REJECT_ORDER. tl_canceledids is ADDING ++ :[{0}]", _tlid));
                                    lock (tl_canceledids)
                                    {
                                        tl_canceledids[_tlid] = true;
                                    }
                                    tl.newCancel(_tlid);
                                }
                            }
                            else
                            {
                                v(String.Format("REJECT_ORDER. could not find KEY:[{0}]", _tlid));
                            }
                        }
                        else // does contain KEY
                        {
                            bool _value = false;
                            if (tl_canceledids.TryGetValue(_tlid, out _value))
                            {
                                if (_value)
                                {
                                    v(String.Format("REJECT_ORDER. tl_canceledids has already sent cancel :[{0}]", _tlid));
                                }
                                else
                                {
                                    v(String.Format("REJECT_ORDER. tl_canceledids is ADDING ++ :[{0}]", _tlid));
                                    lock (tl_canceledids)
                                    {
                                        tl_canceledids[_tlid] = true;
                                    }
                                    tl.newCancel(_tlid);
                                }
                            }
                            else
                            {
                                v(String.Format("REJECT_ORDER. could not find KEY:[{0}]", _tlid));
                            }
                        }

                        break;
                    }
                case REJECT_TYPE.REJECT_CANCEL:
                    {
                        v(String.Format("REJECT_CANCEL.{0}", rejectMsg.Symbol.Value));
                        break;
                    }
                case REJECT_TYPE.REJECT_CANCEL_REPLACE:
                    {
                        v(String.Format("REJECT_CANCEL_REPLACE.{0}", rejectMsg.Symbol.Value));
                        break;
                    }
                default:
                    {
                        break;
                    }
            }
        }

    }

}
