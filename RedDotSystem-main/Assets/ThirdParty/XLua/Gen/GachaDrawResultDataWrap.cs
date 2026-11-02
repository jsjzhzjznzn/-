#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

using XLua;
using System.Collections.Generic;


namespace XLua.CSObjectWrap
{
    using Utils = XLua.Utils;
    public class GachaDrawResultDataWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(GachaDrawResultData);
			Utils.BeginObjectRegister(type, L, translator, 0, 1, 17, 17);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "Fill", _m_Fill);
			
			
			Utils.RegisterFunc(L, Utils.GETTER_IDX, "success", _g_get_success);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "message", _g_get_message);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "pityCount", _g_get_pityCount);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "pityThreshold", _g_get_pityThreshold);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "totalDraws", _g_get_totalDraws);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "count", _g_get_count);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "guaranteedUp", _g_get_guaranteedUp);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "replayed", _g_get_replayed);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "retryable", _g_get_retryable);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "itemIds", _g_get_itemIds);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "itemNames", _g_get_itemNames);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "iconKeys", _g_get_iconKeys);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "rarities", _g_get_rarities);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "isUp", _g_get_isUp);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "isPity", _g_get_isPity);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "isNew", _g_get_isNew);
            Utils.RegisterFunc(L, Utils.GETTER_IDX, "pityKinds", _g_get_pityKinds);
            
			Utils.RegisterFunc(L, Utils.SETTER_IDX, "success", _s_set_success);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "message", _s_set_message);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "pityCount", _s_set_pityCount);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "pityThreshold", _s_set_pityThreshold);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "totalDraws", _s_set_totalDraws);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "count", _s_set_count);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "guaranteedUp", _s_set_guaranteedUp);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "replayed", _s_set_replayed);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "retryable", _s_set_retryable);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "itemIds", _s_set_itemIds);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "itemNames", _s_set_itemNames);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "iconKeys", _s_set_iconKeys);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "rarities", _s_set_rarities);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "isUp", _s_set_isUp);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "isPity", _s_set_isPity);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "isNew", _s_set_isNew);
            Utils.RegisterFunc(L, Utils.SETTER_IDX, "pityKinds", _s_set_pityKinds);
            
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 1, 0, 0);
			
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 1)
				{
					
					var gen_ret = new GachaDrawResultData();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to GachaDrawResultData constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Fill(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    System.Collections.Generic.IReadOnlyList<Common.Network.GachaResultItem> _items = (System.Collections.Generic.IReadOnlyList<Common.Network.GachaResultItem>)translator.GetObject(L, 2, typeof(System.Collections.Generic.IReadOnlyList<Common.Network.GachaResultItem>));
                    
                    gen_to_be_invoked.Fill( _items );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_success(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.success);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_message(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushstring(L, gen_to_be_invoked.message);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_pityCount(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.pityCount);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_pityThreshold(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.pityThreshold);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_totalDraws(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.totalDraws);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_count(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.xlua_pushinteger(L, gen_to_be_invoked.count);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_guaranteedUp(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.guaranteedUp);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_replayed(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.replayed);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_retryable(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                LuaAPI.lua_pushboolean(L, gen_to_be_invoked.retryable);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_itemIds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.itemIds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_itemNames(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.itemNames);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_iconKeys(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.iconKeys);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_rarities(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.rarities);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_isUp(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.isUp);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_isPity(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.isPity);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_isNew(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.isNew);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_pityKinds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                translator.Push(L, gen_to_be_invoked.pityKinds);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_success(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.success = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_message(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.message = LuaAPI.lua_tostring(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_pityCount(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.pityCount = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_pityThreshold(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.pityThreshold = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_totalDraws(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.totalDraws = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_count(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.count = LuaAPI.xlua_tointeger(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_guaranteedUp(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.guaranteedUp = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_replayed(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.replayed = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_retryable(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.retryable = LuaAPI.lua_toboolean(L, 2);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_itemIds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.itemIds = (int[])translator.GetObject(L, 2, typeof(int[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_itemNames(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.itemNames = (string[])translator.GetObject(L, 2, typeof(string[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_iconKeys(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.iconKeys = (string[])translator.GetObject(L, 2, typeof(string[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_rarities(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.rarities = (int[])translator.GetObject(L, 2, typeof(int[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_isUp(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.isUp = (bool[])translator.GetObject(L, 2, typeof(bool[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_isPity(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.isPity = (bool[])translator.GetObject(L, 2, typeof(bool[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_isNew(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.isNew = (bool[])translator.GetObject(L, 2, typeof(bool[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_pityKinds(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			
                GachaDrawResultData gen_to_be_invoked = (GachaDrawResultData)translator.FastGetCSObj(L, 1);
                gen_to_be_invoked.pityKinds = (int[])translator.GetObject(L, 2, typeof(int[]));
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
		
		
    }
}
