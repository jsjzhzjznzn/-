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
    public class ActivityUiWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(ActivityUi);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 49, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "Hex", _m_Hex_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "WithAlpha", _m_WithAlpha_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NewRect", _m_NewRect_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Place", _m_Place_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "PlaceEx", _m_PlaceEx_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Stretch", _m_Stretch_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Resize", _m_Resize_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Clear", _m_Clear_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FindGo", _m_FindGo_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FindRect", _m_FindRect_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FindLabel", _m_FindLabel_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FindScroll", _m_FindScroll_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FindImage", _m_FindImage_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FindButton", _m_FindButton_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "BindButton", _m_BindButton_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetActive", _m_SetActive_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Clone", _m_Clone_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Group", _m_Group_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetGroup", _m_SetGroup_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetBar", _m_SetBar_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NoRaycast", _m_NoRaycast_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NewImage", _m_NewImage_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetRounded", _m_SetRounded_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetSolid", _m_SetSolid_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetSprite", _m_SetSprite_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Tint", _m_Tint_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NewLabel", _m_NewLabel_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NewButton", _m_NewButton_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NewGraphic", _m_NewGraphic_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NewButtonLabel", _m_NewButtonLabel_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "RectOf", _m_RectOf_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "BeginSession", _m_BeginSession_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EndSession", _m_EndSession_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ReleaseClickCallbacks", _m_ReleaseClickCallbacks_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "OnClick", _m_OnClick_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetInteractable", _m_SetInteractable_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "NewScroll", _m_NewScroll_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ScrollContent", _m_ScrollContent_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetScrollHeight", _m_SetScrollHeight_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ScrollToTop", _m_ScrollToTop_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "ItemIcon", _m_ItemIcon_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Rounded", _m_Rounded_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Solid", _m_Solid_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Circle", _m_Circle_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Ring", _m_Ring_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Star", _m_Star_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SoftGlow", _m_SoftGlow_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "VerticalFade", _m_VerticalFade_xlua_st_);
            
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "ActivityUi does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Hex_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 2)) 
                {
                    string _hex = LuaAPI.lua_tostring(L, 1);
                    float _alpha = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = ActivityUi.Hex( _hex, _alpha );
                        translator.PushUnityEngineColor(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 1&& (LuaAPI.lua_isnil(L, 1) || LuaAPI.lua_type(L, 1) == LuaTypes.LUA_TSTRING)) 
                {
                    string _hex = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = ActivityUi.Hex( _hex );
                        translator.PushUnityEngineColor(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to ActivityUi.Hex!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_WithAlpha_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Color _color;translator.Get(L, 1, out _color);
                    float _alpha = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = ActivityUi.WithAlpha( _color, _alpha );
                        translator.PushUnityEngineColor(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NewRect_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& translator.Assignable<UnityEngine.Transform>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = ActivityUi.NewRect( _parent, _name );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 8&& translator.Assignable<UnityEngine.Transform>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 4)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 5)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 6)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 7)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 8)) 
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    float _anchorX = (float)LuaAPI.lua_tonumber(L, 3);
                    float _anchorY = (float)LuaAPI.lua_tonumber(L, 4);
                    float _posX = (float)LuaAPI.lua_tonumber(L, 5);
                    float _posY = (float)LuaAPI.lua_tonumber(L, 6);
                    float _width = (float)LuaAPI.lua_tonumber(L, 7);
                    float _height = (float)LuaAPI.lua_tonumber(L, 8);
                    
                        var gen_ret = ActivityUi.NewRect( _parent, _name, _anchorX, _anchorY, _posX, _posY, _width, _height );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to ActivityUi.NewRect!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Place_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.RectTransform _rt = (UnityEngine.RectTransform)translator.GetObject(L, 1, typeof(UnityEngine.RectTransform));
                    float _anchorX = (float)LuaAPI.lua_tonumber(L, 2);
                    float _anchorY = (float)LuaAPI.lua_tonumber(L, 3);
                    float _posX = (float)LuaAPI.lua_tonumber(L, 4);
                    float _posY = (float)LuaAPI.lua_tonumber(L, 5);
                    float _width = (float)LuaAPI.lua_tonumber(L, 6);
                    float _height = (float)LuaAPI.lua_tonumber(L, 7);
                    
                    ActivityUi.Place( _rt, _anchorX, _anchorY, _posX, _posY, _width, _height );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_PlaceEx_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.RectTransform _rt = (UnityEngine.RectTransform)translator.GetObject(L, 1, typeof(UnityEngine.RectTransform));
                    float _anchorX = (float)LuaAPI.lua_tonumber(L, 2);
                    float _anchorY = (float)LuaAPI.lua_tonumber(L, 3);
                    float _pivotX = (float)LuaAPI.lua_tonumber(L, 4);
                    float _pivotY = (float)LuaAPI.lua_tonumber(L, 5);
                    float _posX = (float)LuaAPI.lua_tonumber(L, 6);
                    float _posY = (float)LuaAPI.lua_tonumber(L, 7);
                    float _width = (float)LuaAPI.lua_tonumber(L, 8);
                    float _height = (float)LuaAPI.lua_tonumber(L, 9);
                    
                    ActivityUi.PlaceEx( _rt, _anchorX, _anchorY, _pivotX, _pivotY, _posX, _posY, _width, _height );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Stretch_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.RectTransform _rt = (UnityEngine.RectTransform)translator.GetObject(L, 1, typeof(UnityEngine.RectTransform));
                    float _left = (float)LuaAPI.lua_tonumber(L, 2);
                    float _top = (float)LuaAPI.lua_tonumber(L, 3);
                    float _right = (float)LuaAPI.lua_tonumber(L, 4);
                    float _bottom = (float)LuaAPI.lua_tonumber(L, 5);
                    
                    ActivityUi.Stretch( _rt, _left, _top, _right, _bottom );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Resize_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.RectTransform _rt = (UnityEngine.RectTransform)translator.GetObject(L, 1, typeof(UnityEngine.RectTransform));
                    float _width = (float)LuaAPI.lua_tonumber(L, 2);
                    float _height = (float)LuaAPI.lua_tonumber(L, 3);
                    
                    ActivityUi.Resize( _rt, _width, _height );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Clear_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    
                    ActivityUi.Clear( _parent );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindGo_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = ActivityUi.FindGo( _root, _path );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindRect_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = ActivityUi.FindRect( _root, _path );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindLabel_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = ActivityUi.FindLabel( _root, _path );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindScroll_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = ActivityUi.FindScroll( _root, _path );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindImage_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = ActivityUi.FindImage( _root, _path );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FindButton_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = ActivityUi.FindButton( _root, _path );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BindButton_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    XLua.LuaFunction _callback = (XLua.LuaFunction)translator.GetObject(L, 3, typeof(XLua.LuaFunction));
                    
                        var gen_ret = ActivityUi.BindButton( _root, _path, _callback );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetActive_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    bool _active = LuaAPI.lua_toboolean(L, 3);
                    
                    ActivityUi.SetActive( _root, _path, _active );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Clone_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.GameObject _template = (UnityEngine.GameObject)translator.GetObject(L, 1, typeof(UnityEngine.GameObject));
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 2, typeof(UnityEngine.Transform));
                    
                        var gen_ret = ActivityUi.Clone( _template, _parent );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Group_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.GameObject _go = (UnityEngine.GameObject)translator.GetObject(L, 1, typeof(UnityEngine.GameObject));
                    float _alpha = (float)LuaAPI.lua_tonumber(L, 2);
                    bool _interactable = LuaAPI.lua_toboolean(L, 3);
                    
                        var gen_ret = ActivityUi.Group( _go, _alpha, _interactable );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetGroup_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _root = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _path = LuaAPI.lua_tostring(L, 2);
                    float _alpha = (float)LuaAPI.lua_tonumber(L, 3);
                    bool _interactable = LuaAPI.lua_toboolean(L, 4);
                    
                        var gen_ret = ActivityUi.SetGroup( _root, _path, _alpha, _interactable );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetBar_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.RectTransform _fill = (UnityEngine.RectTransform)translator.GetObject(L, 1, typeof(UnityEngine.RectTransform));
                    UnityEngine.RectTransform _background = (UnityEngine.RectTransform)translator.GetObject(L, 2, typeof(UnityEngine.RectTransform));
                    float _percent = (float)LuaAPI.lua_tonumber(L, 3);
                    
                    ActivityUi.SetBar( _fill, _background, _percent );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NoRaycast_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.GameObject _go = (UnityEngine.GameObject)translator.GetObject(L, 1, typeof(UnityEngine.GameObject));
                    
                    ActivityUi.NoRaycast( _go );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NewImage_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& translator.Assignable<UnityEngine.Transform>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = ActivityUi.NewImage( _parent, _name );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 8&& translator.Assignable<UnityEngine.Transform>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 4)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 5)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 6)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 7)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 8)) 
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    float _anchorX = (float)LuaAPI.lua_tonumber(L, 3);
                    float _anchorY = (float)LuaAPI.lua_tonumber(L, 4);
                    float _posX = (float)LuaAPI.lua_tonumber(L, 5);
                    float _posY = (float)LuaAPI.lua_tonumber(L, 6);
                    float _width = (float)LuaAPI.lua_tonumber(L, 7);
                    float _height = (float)LuaAPI.lua_tonumber(L, 8);
                    
                        var gen_ret = ActivityUi.NewImage( _parent, _name, _anchorX, _anchorY, _posX, _posY, _width, _height );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to ActivityUi.NewImage!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetRounded_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.Image _image = (UnityEngine.UI.Image)translator.GetObject(L, 1, typeof(UnityEngine.UI.Image));
                    int _radius = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = ActivityUi.SetRounded( _image, _radius );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetSolid_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.Image _image = (UnityEngine.UI.Image)translator.GetObject(L, 1, typeof(UnityEngine.UI.Image));
                    
                        var gen_ret = ActivityUi.SetSolid( _image );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetSprite_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.Image _image = (UnityEngine.UI.Image)translator.GetObject(L, 1, typeof(UnityEngine.UI.Image));
                    UnityEngine.Sprite _sprite = (UnityEngine.Sprite)translator.GetObject(L, 2, typeof(UnityEngine.Sprite));
                    bool _sliced = LuaAPI.lua_toboolean(L, 3);
                    
                        var gen_ret = ActivityUi.SetSprite( _image, _sprite, _sliced );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Tint_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.Image _image = (UnityEngine.UI.Image)translator.GetObject(L, 1, typeof(UnityEngine.UI.Image));
                    UnityEngine.Color _color;translator.Get(L, 2, out _color);
                    
                        var gen_ret = ActivityUi.Tint( _image, _color );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NewLabel_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 4&& translator.Assignable<UnityEngine.Transform>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 4)) 
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    string _text = LuaAPI.lua_tostring(L, 3);
                    int _fontSize = LuaAPI.xlua_tointeger(L, 4);
                    
                        var gen_ret = ActivityUi.NewLabel( _parent, _name, _text, _fontSize );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 6&& translator.Assignable<UnityEngine.Transform>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 4)&& translator.Assignable<UnityEngine.Color>(L, 5)&& translator.Assignable<TMPro.TextAlignmentOptions>(L, 6)) 
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    string _text = LuaAPI.lua_tostring(L, 3);
                    int _fontSize = LuaAPI.xlua_tointeger(L, 4);
                    UnityEngine.Color _color;translator.Get(L, 5, out _color);
                    TMPro.TextAlignmentOptions _align;translator.Get(L, 6, out _align);
                    
                        var gen_ret = ActivityUi.NewLabel( _parent, _name, _text, _fontSize, _color, _align );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 12&& translator.Assignable<UnityEngine.Transform>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 4)&& translator.Assignable<UnityEngine.Color>(L, 5)&& translator.Assignable<TMPro.TextAlignmentOptions>(L, 6)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 7)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 8)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 9)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 10)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 11)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 12)) 
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    string _text = LuaAPI.lua_tostring(L, 3);
                    int _fontSize = LuaAPI.xlua_tointeger(L, 4);
                    UnityEngine.Color _color;translator.Get(L, 5, out _color);
                    TMPro.TextAlignmentOptions _align;translator.Get(L, 6, out _align);
                    float _anchorX = (float)LuaAPI.lua_tonumber(L, 7);
                    float _anchorY = (float)LuaAPI.lua_tonumber(L, 8);
                    float _posX = (float)LuaAPI.lua_tonumber(L, 9);
                    float _posY = (float)LuaAPI.lua_tonumber(L, 10);
                    float _width = (float)LuaAPI.lua_tonumber(L, 11);
                    float _height = (float)LuaAPI.lua_tonumber(L, 12);
                    
                        var gen_ret = ActivityUi.NewLabel( _parent, _name, _text, _fontSize, _color, _align, _anchorX, _anchorY, _posX, _posY, _width, _height );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to ActivityUi.NewLabel!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NewButton_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    string _labelText = LuaAPI.lua_tostring(L, 3);
                    int _fontSize = LuaAPI.xlua_tointeger(L, 4);
                    float _width = (float)LuaAPI.lua_tonumber(L, 5);
                    float _height = (float)LuaAPI.lua_tonumber(L, 6);
                    
                        var gen_ret = ActivityUi.NewButton( _parent, _name, _labelText, _fontSize, _width, _height );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NewGraphic_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.Button _button = (UnityEngine.UI.Button)translator.GetObject(L, 1, typeof(UnityEngine.UI.Button));
                    
                        var gen_ret = ActivityUi.NewGraphic( _button );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NewButtonLabel_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.Button _button = (UnityEngine.UI.Button)translator.GetObject(L, 1, typeof(UnityEngine.UI.Button));
                    
                        var gen_ret = ActivityUi.NewButtonLabel( _button );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RectOf_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 1&& translator.Assignable<UnityEngine.Component>(L, 1)) 
                {
                    UnityEngine.Component _component = (UnityEngine.Component)translator.GetObject(L, 1, typeof(UnityEngine.Component));
                    
                        var gen_ret = ActivityUi.RectOf( _component );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 1&& translator.Assignable<UnityEngine.GameObject>(L, 1)) 
                {
                    UnityEngine.GameObject _gameObject = (UnityEngine.GameObject)translator.GetObject(L, 1, typeof(UnityEngine.GameObject));
                    
                        var gen_ret = ActivityUi.RectOf( _gameObject );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to ActivityUi.RectOf!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_BeginSession_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    ActivityUi.BeginSession(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EndSession_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    ActivityUi.EndSession(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ReleaseClickCallbacks_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    ActivityUi.ReleaseClickCallbacks(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_OnClick_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.Button _button = (UnityEngine.UI.Button)translator.GetObject(L, 1, typeof(UnityEngine.UI.Button));
                    XLua.LuaFunction _callback = (XLua.LuaFunction)translator.GetObject(L, 2, typeof(XLua.LuaFunction));
                    
                        var gen_ret = ActivityUi.OnClick( _button, _callback );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetInteractable_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.Button _button = (UnityEngine.UI.Button)translator.GetObject(L, 1, typeof(UnityEngine.UI.Button));
                    bool _interactable = LuaAPI.lua_toboolean(L, 2);
                    UnityEngine.Color _enabledColor;translator.Get(L, 3, out _enabledColor);
                    
                        var gen_ret = ActivityUi.SetInteractable( _button, _interactable, _enabledColor );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_NewScroll_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.Transform _parent = (UnityEngine.Transform)translator.GetObject(L, 1, typeof(UnityEngine.Transform));
                    string _name = LuaAPI.lua_tostring(L, 2);
                    float _anchorX = (float)LuaAPI.lua_tonumber(L, 3);
                    float _anchorY = (float)LuaAPI.lua_tonumber(L, 4);
                    float _posX = (float)LuaAPI.lua_tonumber(L, 5);
                    float _posY = (float)LuaAPI.lua_tonumber(L, 6);
                    float _width = (float)LuaAPI.lua_tonumber(L, 7);
                    float _height = (float)LuaAPI.lua_tonumber(L, 8);
                    
                        var gen_ret = ActivityUi.NewScroll( _parent, _name, _anchorX, _anchorY, _posX, _posY, _width, _height );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ScrollContent_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.ScrollRect _scroll = (UnityEngine.UI.ScrollRect)translator.GetObject(L, 1, typeof(UnityEngine.UI.ScrollRect));
                    
                        var gen_ret = ActivityUi.ScrollContent( _scroll );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetScrollHeight_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.ScrollRect _scroll = (UnityEngine.UI.ScrollRect)translator.GetObject(L, 1, typeof(UnityEngine.UI.ScrollRect));
                    float _contentHeight = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    ActivityUi.SetScrollHeight( _scroll, _contentHeight );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ScrollToTop_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    UnityEngine.UI.ScrollRect _scroll = (UnityEngine.UI.ScrollRect)translator.GetObject(L, 1, typeof(UnityEngine.UI.ScrollRect));
                    
                    ActivityUi.ScrollToTop( _scroll );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ItemIcon_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _iconKey = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = ActivityUi.ItemIcon( _iconKey );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Rounded_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    int _radius = LuaAPI.xlua_tointeger(L, 1);
                    
                        var gen_ret = ActivityUi.Rounded( _radius );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Solid_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    
                        var gen_ret = ActivityUi.Solid(  );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Circle_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    int _size = LuaAPI.xlua_tointeger(L, 1);
                    
                        var gen_ret = ActivityUi.Circle( _size );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Ring_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    int _size = LuaAPI.xlua_tointeger(L, 1);
                    int _thickness = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = ActivityUi.Ring( _size, _thickness );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Star_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    int _size = LuaAPI.xlua_tointeger(L, 1);
                    
                        var gen_ret = ActivityUi.Star( _size );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SoftGlow_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    int _size = LuaAPI.xlua_tointeger(L, 1);
                    
                        var gen_ret = ActivityUi.SoftGlow( _size );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_VerticalFade_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    int _height = LuaAPI.xlua_tointeger(L, 1);
                    
                        var gen_ret = ActivityUi.VerticalFade( _height );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        
        
		
		
		
		
    }
}
