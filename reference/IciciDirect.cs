// Decompiled with JetBrains decompiler
// Type: Breeze.BreezeConnect
// Assembly: BreezeConnect, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 3FCCF82E-39EE-4499-AF9B-E7A6A51DDC7C
// Assembly location: D:\Share Market\MoneyFlow\packages\com.breeze.breezeconnect.1.0.1\lib\netstandard2.0\BreezeConnect.dll
// XML documentation location: D:\Share Market\MoneyFlow\packages\com.breeze.breezeconnect.1.0.1\lib\netstandard2.0\BreezeConnect.xml

using APILibrary;
using Breeze;
using RestSharp;
using SocketIOClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FeedLibrary.Common
{
    public class IciciDirect
    {
        private string _apiKey;
        private ApificationBreeze _apiHandler = new ApificationBreeze();
        private SocketEventBreeze _socketHandler = new SocketEventBreeze();
        private Dictionary<string, string>[] _stockScriptDictList;
        public SocketIO _socket;
        private readonly IGeekConfigManager _configuration;

        public IciciDirect(string apiKey, IGeekConfigManager configuration)
        {
           this._apiKey = apiKey;
            this._configuration = configuration;
        }

        public void generateSession(string secretKey, string sessionToken, bool debug = false)
        {
            RestClient restClient = new RestClient("https://api.icicidirect.com/breezeapi/api/v1/");
            RestRequest restRequest1 = new RestRequest("customerdetails", 0);
            restRequest1.AddHeader("Content-Type", "application/json");
            string str1 = JsonSerializer.Serialize(new Dictionary<string, string>()
      {
        {
          "SessionToken",
         sessionToken
        },
        {
          "AppKey",
         _apiKey
        }
      }, (JsonSerializerOptions)null);
            restRequest1.AddParameter("application/json", str1, (ParameterType)3, true);
            RestRequest restRequest2 = restRequest1;
            RestResponse restResponse = restClient.Execute(restRequest2);
            if (restResponse.StatusCode != HttpStatusCode.OK)
                return;
            Dictionary<string, object> dictionary = JsonSerializer.Deserialize<Dictionary<string, object>>(restResponse.Content, (JsonSerializerOptions)null);
            object obj1;
            if (dictionary.TryGetValue("Status", out obj1) && int.Parse(JsonSerializer.Serialize(obj1, (JsonSerializerOptions)null)) != 200)
            {
                object obj2;
                dictionary.TryGetValue("Error", out obj2);
               // Console.WriteLine("Fail to generate session.\nError: " + obj2.ToString());
            }
            else
            {
                object obj3;
                dictionary.TryGetValue("Success", out obj3);
                object obj4;
                JsonSerializer.Deserialize<Dictionary<string, object>>(obj3.ToString(), (JsonSerializerOptions)null).TryGetValue("session_token", out obj4);
                string str2 = obj4.ToString();
                _apiHandler.setSession(_apiKey, secretKey, str2);
                string str3 = Encoding.UTF8.GetString(Convert.FromBase64String(str2));
                _socketHandler.setSession(str3.Split(':')[0], str3.Split(':')[1], getStockScriptList(), (debug ? 1 : 0) != 0, _apiKey);
            }
        }

        private Dictionary<string, string[]>[] getStockScriptList()
        {
            _stockScriptDictList = new Dictionary<string, string>[5]
            {
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>()
            };
            Dictionary<string, string[]>[] stockScriptList = new Dictionary<string, string[]>[5]
            {
        new Dictionary<string, string[]>(),
        new Dictionary<string, string[]>(),
        new Dictionary<string, string[]>(),
        new Dictionary<string, string[]>(),
        new Dictionary<string, string[]>()
            };
            using (WebClient webClient = new WebClient())
            {
                // string str1 = Encoding.UTF8.GetString(webClient.DownloadData("https://traderweb.icicidirect.com/Content/File/txtFile/ScripFile/StockScriptNew.csv"));
                string str1 = Encoding.UTF8.GetString(File.ReadAllBytes(this._configuration.ICICIStockLocation+"\\StockScriptNew.csv"));

                char[] chArray = new char[1] { '\n' };
                foreach (string str2 in str1.Split(chArray))
                {
                    if (!(str2 == ""))
                    {
                        string[] strArray = str2.Split(',');
                        if (!(strArray[5] == "0") && !(strArray[5] == "00") && !(strArray[5] == "NA"))
                        {
                            if (strArray[2] == "BSE")
                            {
                                _stockScriptDictList[0].Add(strArray[3], strArray[5]);
                                string[] valueObject = new string[2]
                                {
                  strArray[3],
                  strArray[1]
                                };
                                stockScriptList[0] = Miscellaneous.tryAddToDictionary(strArray[5], valueObject, stockScriptList[0]);
                            }
                            else if (strArray[2] == "NSE")
                            {
                                _stockScriptDictList[1].Add(strArray[3], strArray[5]);
                                string[] valueObject = new string[2]
                                {
                  strArray[3],
                  strArray[1]
                                };
                                stockScriptList[1] = Miscellaneous.tryAddToDictionary(strArray[5], valueObject, stockScriptList[1]);
                            }
                            else if (strArray[2] == "NDX")
                            {
                                _stockScriptDictList[2].Add(strArray[7], strArray[5]);
                                string[] valueObject = new string[2]
                                {
                  strArray[7],
                  strArray[1]
                                };
                                stockScriptList[2] = Miscellaneous.tryAddToDictionary(strArray[5], valueObject, stockScriptList[2]);
                            }
                            else if (strArray[2] == "MCX")
                            {
                                _stockScriptDictList[3].Add(strArray[7], strArray[5]);
                                string[] valueObject = new string[2]
                                {
                  strArray[7],
                  strArray[1]
                                };
                                stockScriptList[3] = Miscellaneous.tryAddToDictionary(strArray[5], valueObject, stockScriptList[3]);
                            }
                            else if (strArray[2] == "NFO")
                            {
                                _stockScriptDictList[4].Add(strArray[7], strArray[5]);
                                string[] valueObject = new string[2]
                                {
                  strArray[7],
                  strArray[1]
                                };
                                stockScriptList[4] = Miscellaneous.tryAddToDictionary(strArray[5], valueObject, stockScriptList[4]);
                            }
                        }
                    }
                }
            }
            return stockScriptList;
        }

        public async Task<Dictionary<string, object>> wsConnectAsync()
        {
            if (!_socketHandler.hasSession())
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "Session not generated. Please generate session."
          }
        };
            _socket = await _socketHandler.Connect();
            if (_socketHandler.isConnected())
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "Socket connection already established."
          }
        };

            if (_socketHandler.isConnected())
                return new Dictionary<string, object>()
        {
          {
            "Success",
             "Socket connection established."
          },
          {
            "Status",
             200
          },
          {
            "Error",
             null
          }
        };
            return new Dictionary<string, object>()
      {
        {
          "Success",
           null
        },
        {
          "Status",
           500
        },
        {
          "Error",
           "Fail to establish Socket connection."
        }
      };
        }

        public async Task<Dictionary<string, object>> subscribeFeedsAsync(
          string stockToken)
        {
            if (stockToken == null || stockToken.Length == 0)
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "Stock-Token cannot be empty"
          }
        };
            await _socketHandler.watch(new string[1]
            {
        stockToken
            });
            return new Dictionary<string, object>()
      {
        {
          "Success",
           "Stock " + stockToken + " subscribed successfully"
        },
        {
          "Status",
           200
        },
        {
          "Error",
           null
        }
      };
        }

        public Dictionary<string, object> subscribeFeedsAsync(bool getOrderNotification = false)
        {
            if (!getOrderNotification)
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "getOrderNotification should be true"
          }
        };
            if (_socketHandler.isOrderNotificationSubscribed())
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "Order Notification already subscribed"
          }
        };
            _socketHandler.setOrderNotificationSubscription(getOrderNotification);
            return new Dictionary<string, object>()
      {
        {
          "Success",
           "Order Notification subscribed successfully"
        },
        {
          "Status",
           200
        },
        {
          "Error",
           null
        }
      };
        }

        public async Task<Dictionary<string, object>> subscribeFeedsAsync(
          string exchangeCode,
          string stockCode,
          string productType,
          string expiryDate,
          string strikePrice,
          string right,
          bool getExchangeQuotes,
          bool getMarketDepth)
        {
            string[] tokenObject = getStockTokenValue(exchangeCode, stockCode, productType, expiryDate, strikePrice, right, getExchangeQuotes, getMarketDepth);
            if (tokenObject.Length != 2)
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "Stock-Token not found for Stock-Code " + stockCode + "."
          }
        };
            if (!string.IsNullOrEmpty(tokenObject[0]))
                await _socketHandler.watch(new string[1]
                {
          tokenObject[0]
                });
            if (!string.IsNullOrEmpty(tokenObject[1]))
                await _socketHandler.watch(new string[1]
                {
          tokenObject[1]
                });
            return new Dictionary<string, object>()
      {
        {
          "Success",
           "Stock-Code " + stockCode + " subscribed successfully."
        },
        {
          "Status",
           200
        },
        {
          "Error",
           null
        }
      };
        }

        public async Task<Dictionary<string, object>> unsubscribeFeedsAsync(
          string stockToken)
        {
            if (stockToken == null || stockToken.Length == 0)
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "Stock-Token cannot be empty"
          }
        };
            await _socketHandler.unWatch(new string[1]
            {
        stockToken
            });
            return new Dictionary<string, object>()
      {
        {
          "Success",
           "Stock " + stockToken + " unsubscribed successfully"
        },
        {
          "Status",
           200
        },
        {
          "Error",
           null
        }
      };
        }

        public Dictionary<string, object> unsubscribeFeeds(bool getOrderNotification = true)
        {
            if (getOrderNotification)
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "getOrderNotification should be false"
          }
        };
            if (!_socketHandler.isOrderNotificationSubscribed())
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "Order Notification is not subscribed"
          }
        };
            _socketHandler.setOrderNotificationSubscription(getOrderNotification);
            return new Dictionary<string, object>()
      {
        {
          "Success",
           "Order Notification unsubscribed successfully"
        },
        {
          "Status",
           200
        },
        {
          "Error",
           null
        }
      };
        }

        public async Task<Dictionary<string, object>> unsubscribeFeedsAsync(
          string exchangeCode,
          string stockCode,
          string productType,
          string expiryDate,
          string strikePrice,
          string right,
          bool getExchangeQuotes,
          bool getMarketDepth)
        {
            string[] tokenObject = getStockTokenValue(exchangeCode, stockCode, productType, expiryDate, strikePrice, right, getExchangeQuotes, getMarketDepth);
            if (tokenObject.Length != 2)
                return new Dictionary<string, object>()
        {
          {
            "Success",
             null
          },
          {
            "Status",
             500
          },
          {
            "Error",
             "Stock-Token not found for Stock-Code " + stockCode + "."
          }
        };
            if (!string.IsNullOrEmpty(tokenObject[0]))
                await _socketHandler.unWatch(new string[1]
                {
          tokenObject[0]
                });
            if (!string.IsNullOrEmpty(tokenObject[1]))
                await _socketHandler.unWatch(new string[1]
                {
          tokenObject[1]
                });
            return new Dictionary<string, object>()
      {
        {
          "Success",
           "Stock-Code " + stockCode + " unsubscribed successfully."
        },
        {
          "Status",
           200
        },
        {
          "Error",
           null
        }
      };
        }

        private string[] getStockTokenValue(
          string exchangeCode,
          string stockCode,
          string productType,
          string expiryDate,
          string strikePrice,
          string right,
          bool getExchangeQuotes,
          bool getMarketDepth)
        {
            if (!getExchangeQuotes && !getMarketDepth)
                throw new Exception("Either getExchangeQuotes must be true or getMarketDepth must be true");
            string str1;
            if (!new Dictionary<string, string>()
      {
        {
          "BSE",
          "1."
        },
        {
          "NSE",
          "4."
        },
        {
          "NDX",
          "13."
        },
        {
          "MCX",
          "6."
        },
        {
          "NFO",
          "4."
        }
      }.TryGetValue(exchangeCode, out str1))
                str1 = "";
            if (str1 == null || str1.Length == 0)
                throw new Exception("Exchange Code allowed are 'BSE', 'NSE', 'NDX', 'MCX' or 'NFO'.");
            switch (stockCode)
            {
                case "":
                case null:
                    throw new Exception("Stock-Code cannot be empty.");
                default:
                    string str2 = "";
                    if (exchangeCode.ToLower() == "bse")
                        _stockScriptDictList[0].TryGetValue(stockCode, out str2);
                    else if (exchangeCode.ToLower() == "nse")
                    {
                        _stockScriptDictList[1].TryGetValue(stockCode, out str2);
                    }
                    else
                    {
                        switch (expiryDate)
                        {
                            case "":
                            case null:
                                throw new Exception("Expiry-Date cannot be empty for given Exchange-Code.");
                            default:
                                string str3;
                                if (productType.ToLower() == "futures")
                                {
                                    str3 = "FUT";
                                }
                                else
                                {
                                    if (!(productType.ToLower() == "options"))
                                        throw new Exception("Product-Type should either be Futures or Options for given Exchange-Code.");
                                    str3 = "OPT";
                                }
                                string key = str3 + "-" + stockCode + "-" + expiryDate;
                                if (productType.ToLower() == "options")
                                {
                                    switch (strikePrice)
                                    {
                                        case "":
                                        case null:
                                            throw new Exception("Strike Price cannot be empty for Product-Type 'Options'.");
                                        default:
                                            string str4 = key + "-" + strikePrice;
                                            if (right.ToLower() == "put")
                                            {
                                                key = str4 + "-PE";
                                                break;
                                            }
                                            if (!(right.ToLower() == "call"))
                                                throw new Exception("Rights should either be Put or Call for Product-Type 'Options'.");
                                            key = str4 + "-CE";
                                            break;
                                    }
                                }
                                if (exchangeCode.ToLower() == "ndx")
                                {
                                    _stockScriptDictList[2].TryGetValue(key, out str2);
                                    break;
                                }
                                if (exchangeCode.ToLower() == "mcx")
                                {
                                    _stockScriptDictList[3].TryGetValue(key, out str2);
                                    break;
                                }
                                if (exchangeCode.ToLower() == "nfo")
                                {
                                    _stockScriptDictList[4].TryGetValue(key, out str2);
                                    break;
                                }
                                break;
                        }
                    }
                    if (string.IsNullOrEmpty(str2))
                        throw new Exception("Stock-Code not found.");
                    string str5 = "";
                    if (getExchangeQuotes)
                        str5 = str5 + "1!" + str2;
                    string str6 = "";
                    if (getMarketDepth)
                        str6 = str1 + "2!" + str2;
                    return new string[2] { str5, str6 };
            }
        }

        public void ticker(Action<object> callback)
        {
            while (true)
            {
                if (_socket == null)
                {
                    _socket = _socketHandler.GetSocketIO();
                    if (_socket != null)
                    {
                        if (!_socket.Connected)
                            goto label_5;
                    }
                    else
                        break;
                }
                _socket.On("stock", response =>
                callback(
                    _socketHandler.parseData(response.GetValue(0))
                    )
                );
                _socket.On("order", response =>
                {
                    if (!_socketHandler.isOrderNotificationSubscribed())
                        return;
                    callback(_socketHandler.parseOrderData(
                        response.GetValue(0)
                        ));
                });

            }
            throw new Exception("Socket not connected. Cannot return any ticks.");
        label_5:
            throw new Exception("Socket not connected. Cannot return any ticks.");
        }

        public Dictionary<string, object> getCustomerDetail(string apiSession) => _apiHandler.getCustomerDetail(apiSession);

        public Dictionary<string, object> getDematHoldings() => _apiHandler.getDematHoldings();

        public Dictionary<string, object> getFunds() => _apiHandler.getFunds();

        public Dictionary<string, object> setFunds(
          string transactionType,
          string amount,
          string segment)
        {
            return _apiHandler.setFunds(transactionType, amount, segment);
        }

        public Dictionary<string, object> getHistoricalData(
          string interval,
          string fromDate,
          string toDate,
          string stockCode,
          string exchangeCode,
          string productType = "",
          string expiryDate = "",
          string right = "",
          string strikePrice = "")
        {
            return _apiHandler.getHistoricalData(interval, fromDate, toDate, stockCode, exchangeCode, productType, expiryDate, right, strikePrice);
        }

        public Dictionary<string, object> addMargin(
          string exchangeCode,
          string productType,
          string stockCode,
          string coverQuantity,
          string settlementId,
          string addAmount,
          string marginAmount,
          string openQuantity,
          string categoryIndexPerStock,
          string expiryDate,
          string right,
          string contractTag,
          string strikePrice,
          string segmentCode)
        {
            return _apiHandler.addMargin(exchangeCode, productType, stockCode, coverQuantity, settlementId, addAmount, marginAmount, openQuantity, categoryIndexPerStock, expiryDate, right, contractTag, strikePrice, segmentCode);
        }

        public Dictionary<string, object> getMargin(string exchangeCode) => _apiHandler.getMargin(exchangeCode);

        public Dictionary<string, object> placeOrder(
          string stockCode,
          string exchangeCode,
          string productType,
          string action,
          string orderType,
          string stoploss,
          string quantity,
          string price,
          string validity,
          string validityDate,
          string disclosedQuantity,
          string expiryDate,
          string right,
          string strikePrice,
          string userRemark)
        {
            return _apiHandler.placeOrder(stockCode, exchangeCode, productType, action, orderType, stoploss, quantity, price, validity, validityDate, disclosedQuantity, expiryDate, right, strikePrice, userRemark);
        }

        public Dictionary<string, object> getOrderDetail(string exchangeCode, string orderId) => _apiHandler.getOrderDetail(exchangeCode, orderId);

        public Dictionary<string, object> getOrderList(
          string exchangeCode,
          string fromDate,
          string toDate)
        {
            return _apiHandler.getOrderList(exchangeCode, fromDate, toDate);
        }

        public Dictionary<string, object> cancelOrder(string exchangeCode, string orderId) => _apiHandler.cancelOrder(exchangeCode, orderId);

        public Dictionary<string, object> modifyOrder(
          string orderId,
          string exchangeCode,
          string orderType,
          string stoploss,
          string quantity,
          string price,
          string validity,
          string disclosedQuantity,
          string validityDate)
        {
            return _apiHandler.modifyOrder(orderId, exchangeCode, orderType, stoploss, quantity, price, validity, disclosedQuantity, validityDate);
        }

        public Dictionary<string, object> getPortfolioHoldings(
          string exchangeCode,
          string fromDate,
          string toDate,
          string stockCode = "",
          string portfolioType = "")
        {
            return _apiHandler.getPortfolioHoldings(exchangeCode, fromDate, toDate, stockCode, portfolioType);
        }

        public Dictionary<string, object> getPortfolioPositions() => _apiHandler.getPortfolioPositions();

        public Dictionary<string, object> getQuotes(
          string stockCode,
          string exchangeCode,
          string expiryDate,
          string productType,
          string right,
          string strikePrice)
        {
            return _apiHandler.getQuotes(stockCode, exchangeCode, expiryDate, productType, right, strikePrice);
        }

        public Dictionary<string, object> getOptionChainQuotes(
          string stockCode,
          string exchangeCode,
          string expiryDate,
          string productType,
          string right,
          string strikePrice)
        {
            return _apiHandler.getOptionChainQuotes(stockCode, exchangeCode, expiryDate, productType, right, strikePrice);
        }

        public Dictionary<string, object> squareOff(
          string sourceFlag,
          string stockCode,
          string exchangeCode,
          string quantity,
          string price,
          string action,
          string orderType,
          string validity,
          string stoploss,
          string disclosedQuantity,
          string protectionPercentage,
          string settlementId,
          string marginAmount,
          string openQuantity,
          string coverQuantity,
          string productType,
          string expiryDate,
          string right,
          string strikePrice,
          string validityDate,
          string tradePassword,
          string aliasName)
        {
            return _apiHandler.squareOff(sourceFlag, stockCode, exchangeCode, quantity, price, action, orderType, validity, stoploss, disclosedQuantity, protectionPercentage, settlementId, marginAmount, openQuantity, coverQuantity, productType, expiryDate, right, strikePrice, validityDate, tradePassword, aliasName);
        }

        public Dictionary<string, object> getTradeList(
          string fromDate,
          string toDate,
          string exchangeCode,
          string productType,
          string action,
          string stockCode)
        {
            return _apiHandler.getTradeList(fromDate, toDate, exchangeCode, productType, action, stockCode);
        }

        public Dictionary<string, object> getTradeDetail(string exchangeCode, string orderId) => _apiHandler.getTradeDetail(exchangeCode, orderId);
    }
}
