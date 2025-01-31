﻿#define ENABLE_LOG
/*
 * Copyright (c) 2014 - 2019 t_saki@serenegiant.com 
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

#if UNITY_ANDROID && UNITY_2018_3_OR_NEWER
using UnityEngine.Android;
#endif

namespace Serenegiant.UVC
{
	[RequireComponent(typeof(AndroidUtils))]
	public class UVCManager : MonoBehaviour
	{
		private const string TAG = "UVCManager#";
		private const string FQCN_DETECTOR = "com.serenegiant.usb.DeviceDetectorFragment";
		private const int DEFAULT_FRAME_TYPE = 0x07;
	
		/**
		 * IUVCSelectorがセットされていないとき
		 * またはIUVCSelectorが解像度選択時にnullを
		 * 返したときのデフォルトの解像度(幅)
		*/
		public Int32 DefaultWidth = 1280;
		/**
		 * IUVCSelectorがセットされていないとき
		 * またはIUVCSelectorが解像度選択時にnullを
		 * 返したときのデフォルトの解像度(高さ)
		 */
		public Int32 DefaultHeight = 720;
		/**
		 * UVC機器とのネゴシエーション時に
		 * H.264を優先してネゴシエーションするかどうか
		 * Android実機のみ有効
		 * true:	H.264 > MJPEG > YUV
		 * false:	MJPEG > H.264 > YUV
		 */
		public bool PreferH264 = false;

		/**
		 * UVC関係のイベンドハンドラー
		 */
		[SerializeField, ComponentRestriction(typeof(IUVCDrawer))]
		public Component[] UVCDrawers;

		/**
		 * 使用中のカメラ情報を保持するホルダークラス
		 */
		public class CameraInfo
		{
			internal readonly UVCDevice device;
			internal Texture previewTexture;
			internal Int32 activeId;
			private Int32 currentWidth;
			private Int32 currentHeight;


			internal CameraInfo(UVCDevice device)
			{
				this.device = device;
			}

			/**
			 * 機器idを取得
			 */
			public Int32 Id{
				get { return device.id;  }
			}
	
			/**
			 * 機器名を取得
			 */
			public string DeviceName
			{
				get { return device.name; }
			}

			/**
			 * ベンダーIDを取得
			 */
			public int Vid
			{
				get { return device.vid; }
			}

			/**
			 * プロダクトIDを取得
			 */
			public int Pid
			{
				get { return device.pid; }
			}

			/**
			 * 映像取得中かどうか
			 */
			public bool IsPreviewing
			{
				get { return (activeId != 0) && (previewTexture != null); }
			}

			/**
			 * 現在の解像度(幅)
			 * プレビュー中でなければ0
			 */
			public Int32 CurrentWidth
			{
				get { return currentWidth; }
			}

			/**
			 * 現在の解像度(高さ)
			 * プレビュー中でなければ0
			 */
			public Int32 CurrentHeight
			{
				get { return currentHeight; }
			}

			/**
			 * 現在の解像度を変更
			 * @param width
			 * @param height
			 */
			internal void SetSize(Int32 width, Int32 height)
			{
				currentWidth = width;
				currentHeight = height;
			}

			public override string ToString()
			{
				return $"{base.ToString()}({currentWidth}x{currentHeight},id={Id},activeId={activeId},IsPreviewing={IsPreviewing})";
			}

			/**
			 * レンダーイベント処理用
			 * コールーチンとして実行される
			 */
			internal IEnumerator OnRender()
			{
				var renderEventFunc = GetRenderEventFunc();
				for (; activeId != 0;)
				{
					yield return new WaitForEndOfFrame();
					GL.IssuePluginEvent(renderEventFunc, activeId);
				}
				yield break;
			}
		}

		/**
		 * メインスレッド上で実行するためのSynchronizationContextインスタンス
		 */
		private SynchronizationContext mainContext;
		/**
		 * 端末に接続されたUVC機器の状態が変化した時のイベントコールバックを受け取るデリゲーター
		 */
		private OnDeviceChangedFunc callback;
		/**
		 * 端末に接続されたUVC機器利すると
		 */
		private List<UVCDevice> attachedDevices = new List<UVCDevice>();
		/**
		 * 映像取得中のUVC機器のマップ
		 * 機器識別用のid - CameraInfoペアを保持する
		 */
		private Dictionary<Int32, CameraInfo> cameraInfos = new Dictionary<int, CameraInfo>();

		//--------------------------------------------------------------------------------
		// UnityEngineからの呼び出し
		//--------------------------------------------------------------------------------
		// Start is called before the first frame update
		IEnumerator Start()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Start:");
#endif
			mainContext = SynchronizationContext.Current;
			callback = new OnDeviceChangedFunc(OnDeviceChanged);
			Register(callback);
	
			yield return Initialize();
		}

#if (!NDEBUG && DEBUG && ENABLE_LOG)
		void OnApplicationFocus()
		{
			Console.WriteLine($"{TAG}OnApplicationFocus:");
		}
#endif

#if (!NDEBUG && DEBUG && ENABLE_LOG)
		void OnApplicationPause(bool pauseStatus)
		{
			Console.WriteLine($"{TAG}OnApplicationPause:{pauseStatus}");
		}
#endif

#if (!NDEBUG && DEBUG && ENABLE_LOG)
		void OnApplicationQuits()
		{
			Console.WriteLine($"{TAG}OnApplicationQuits:");
		}
#endif

		void OnDestroy()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnDestroy:");
#endif
			StopAll();
			Unregister(callback);
			callback = null;
		}

		//--------------------------------------------------------------------------------
		// UVC機器接続状態が変化したときのプラグインからのコールバック関数
		//--------------------------------------------------------------------------------
		public void OnDeviceChanged(IntPtr devicePtr, bool attached)
		{
			var id = UVCDevice.GetId(devicePtr);
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnDeviceChanged:id={id},attached={attached}");
#endif
			if (attached)
			{
				UVCDevice device = new UVCDevice(devicePtr);
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}OnDeviceChanged:device={device.ToString()}");
#endif
				if (HandleOnAttachEvent(device))
				{
					attachedDevices.Add(device);
					StartPreview(device);
				}
			}
			else
			{
				var found = attachedDevices.Find(item =>
				{
					return item != null && item.id == id;
				});
				if (found != null)
				{
					HandleOnDetachEvent(found);
					StopPreview(found);
					attachedDevices.Remove(found);
				}
			}
		}

		//================================================================================
		/**
		 * 接続中のUVC機器一覧を取得
		 * @return 接続中のUVC機器一覧List
		 */
		public List<CameraInfo> GetAttachedDevices()
		{
			var result = new List<CameraInfo>(cameraInfos.Count);

			foreach (var info in cameraInfos.Values)
			{
				result.Add(info);
			}

			return result;
		}

//		/**
//		 * 対応解像度を取得
//		 * @param camera 対応解像度を取得するUVC機器を指定
//		 * @return 対応解像度 既にカメラが取り外されている/closeしているのであればnull
//		 */
//		public SupportedFormats GetSupportedVideoSize(CameraInfo camera)
//		{
//			var info = (camera != null) ? Get(camera.DeviceName) : null;
//			if ((info != null) && info.IsOpen)
//			{
//				return GetSupportedVideoSize(info.DeviceName);
//			}
//			else
//			{
//				return null;
//			}
//		}

//		/**
//		 * 解像度を変更
//		 * @param 解像度を変更するUVC機器を指定
//		 * @param 変更する解像度を指定, nullならデフォルトに戻す
//		 * @param 解像度が変更されたかどうか
//		 */
//		public bool SetVideoSize(CameraInfo camera, SupportedFormats.Size size)
//		{
//			var info = (camera != null) ? Get(camera.DeviceName) : null;
//			var width = size != null ? size.Width : DefaultWidth;
//			var height = size != null ? size.Height : DefaultHeight;
//			if ((info != null) && info.IsPreviewing)
//			{
//				if ((width != info.CurrentWidth) || (height != info.CurrentHeight))
//				{   // 解像度が変更になるとき
//					StopPreview(info.DeviceName);
//					StartPreview(info.DeviceName, width, height);
//					return true;
//				}
//			}
//			return false;
//		}

		private void StartPreview(UVCDevice device)
		{
			var info = CreateIfNotExist(device);
			if ((info != null) && !info.IsPreviewing) {

				int width = DefaultWidth;
				int height = DefaultHeight;

//				var supportedVideoSize = GetSupportedVideoSize(deviceName);
//				if (supportedVideoSize == null)
//				{
//					throw new ArgumentException("fauled to get supported video size");
//				}

//				// 解像度の選択処理
//				if ((UVCDrawers != null) && (UVCDrawers.Length > 0))
//				{
//					foreach (var drawer in UVCDrawers)
//					{
//						if ((drawer is IUVCDrawer) && ((drawer as IUVCDrawer).CanDraw(this, info.device)))
//						{
//							var size = (drawer as IUVCDrawer).OnUVCSelectSize(this, info.device, supportedVideoSize);
//#if (!NDEBUG && DEBUG && ENABLE_LOG)
//							Console.WriteLine($"{TAG}StartPreview:selected={size}");
//#endif
//							if (size != null)
//							{   // 一番最初に見つかった描画可能なIUVCDrawersがnull以外を返せばそれを使う
//								width = size.Width;
//								height = size.Height;
//								break;
//							}
//						}
//					}
//				}

				// FIXME 対応解像度の確認処理
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}StartPreview:({width}x{height}),id={device.id}");
#endif
				Resize(device.id, DEFAULT_FRAME_TYPE, width, height);
				info.SetSize(width, height);
				info.activeId = device.id;
				mainContext.Post(__ =>
				{   // テクスチャの生成はメインスレッドで行わないといけない
#if (!NDEBUG && DEBUG && ENABLE_LOG)
					Console.WriteLine($"{TAG}映像受け取り用テクスチャ生成:({width}x{height})");
#endif
					Texture2D tex = new Texture2D(
							width, height,
							TextureFormat.ARGB32,
							false, /* mipmap */
							true /* linear */);
					tex.filterMode = FilterMode.Point;
					tex.Apply();
					info.previewTexture = tex;
					var nativeTexPtr = info.previewTexture.GetNativeTexturePtr();
					Start(device.id, nativeTexPtr.ToInt32());
					HandleOnStartPreviewEvent(info);
					StartCoroutine(info.OnRender());
				}, null);
			}
		}

		private void StopPreview(UVCDevice device) {
			var info = Get(device);
			if ((info != null) && info.IsPreviewing)
			{
				mainContext.Post(__ =>
				{
					HandleOnStopPreviewEvent(info);
					Stop(device.id);
					StopCoroutine(info.OnRender());
					info.SetSize(0, 0);
					info.activeId = 0;
				}, null);
			}
		}


		private void StopAll() {
			List<CameraInfo> values = new List<CameraInfo>(cameraInfos.Values);
			foreach (var info in values)
			{
				StopPreview(info.device);
			}
		}

		//--------------------------------------------------------------------------------
		/**
		 * UVC機器が接続されたときの処理の実体
		 * @param info
		 * @return true: 接続されたUVC機器を使用する, false: 接続されたUVC機器を使用しない
		 */
		private bool HandleOnAttachEvent(UVCDevice device/*NonNull*/)
		{
			if ((UVCDrawers == null) || (UVCDrawers.Length == 0))
			{   // IUVCDrawerが割り当てられていないときはtrue(接続されたUVC機器を使用する)を返す
				return true;
			}
			else
			{
				bool hasDrawer = false;
				foreach (var drawer in UVCDrawers)
				{
					if (drawer is IUVCDrawer)
					{
						hasDrawer = true;
						if ((drawer as IUVCDrawer).OnUVCAttachEvent(this, device))
						{   // どれか1つのIUVCDrawerがtrueを返せばtrue(接続されたUVC機器を使用する)を返す
							return true;
						}
					}
				}
				// IUVCDrawerが割り当てられていないときはtrue(接続されたUVC機器を使用する)を返す
				return !hasDrawer;
			}
		}

		/**
		 * UVC機器が取り外されたときの処理の実体
		 * @param info
		 */
		private void HandleOnDetachEvent(UVCDevice device/*NonNull*/)
		{
			if ((UVCDrawers != null) && (UVCDrawers.Length > 0))
			{
				foreach (var drawer in UVCDrawers)
				{
					if (drawer is IUVCDrawer)
					{
						(drawer as IUVCDrawer).OnUVCDetachEvent(this, device);
					}
				}
			}
		}

		/**
		 * UVC機器からの映像取得を開始した
		 * @param args UVC機器の識別文字列
		 */
		void HandleOnStartPreviewEvent(CameraInfo info)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}HandleOnStartPreviewEvent:({info})");
#endif
			if ((info != null) && info.IsPreviewing && (UVCDrawers != null))
			{
				foreach (var drawer in UVCDrawers)
				{
					if ((drawer is IUVCDrawer) && (drawer as IUVCDrawer).CanDraw(this, info.device))
					{
						(drawer as IUVCDrawer).OnUVCStartEvent(this, info.device, info.previewTexture);
					}
				}
			} else {
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}HandleOnStartPreviewEvent:No UVCDrawers");
#endif
			}
		}

		/**
		 * UVC機器からの映像取得を終了した
		 * @param args UVC機器の識別文字列
		 */
		void HandleOnStopPreviewEvent(CameraInfo info)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}HandleOnStopPreviewEvent:({info})");
#endif
			if (UVCDrawers != null)
			{
				foreach (var drawer in UVCDrawers)
				{
					if ((drawer is IUVCDrawer) && (drawer as IUVCDrawer).CanDraw(this, info.device))
					{
						(drawer as IUVCDrawer).OnUVCStopEvent(this, info.device);
					}
				}
			}
		}

		//--------------------------------------------------------------------------------
		/**
		 * 指定したUVC識別文字列に対応するCameraInfoを取得する
		 * まだ登録させていなければ新規作成する
		 * @param deviceName UVC機器識別文字列
		 * @param CameraInfoを返す
		 */
		/*NonNull*/
		private CameraInfo CreateIfNotExist(UVCDevice device)
		{
			if (!cameraInfos.ContainsKey(device.id))
			{
				cameraInfos[device.id] = new CameraInfo(device);
			}
			return cameraInfos[device.id];
		}

		/**
		 * 指定したUVC識別文字列に対応するCameraInfoを取得する
		 * @param deviceName UVC機器識別文字列
		 * @param 登録してあればCameraInfoを返す、登録されていなければnull
		 */
		/*Nullable*/
		private CameraInfo Get(UVCDevice device)
		{
			return cameraInfos.ContainsKey(device.id) ? cameraInfos[device.id] : null;
		}


		//--------------------------------------------------------------------------------
		/**
		 * プラグインを初期化
		 * パーミッションの確認を行って取得できれば実際のプラグイン初期化処理#InitPluginを呼び出す
		 */
		private IEnumerator Initialize()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Initialize:");
#endif
			if (AndroidUtils.CheckAndroidVersion(28))
			{
				yield return AndroidUtils.GrantCameraPermission((string permission, AndroidUtils.PermissionGrantResult result) =>
				{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
					Console.WriteLine($"{TAG}OnPermission:{permission}={result}");
#endif
					switch (result)
					{
						case AndroidUtils.PermissionGrantResult.PERMISSION_GRANT:
							InitPlugin();
							break;
						case AndroidUtils.PermissionGrantResult.PERMISSION_DENY:
							if (AndroidUtils.ShouldShowRequestPermissionRationale(AndroidUtils.PERMISSION_CAMERA))
							{
								// パーミッションを取得できなかった
								// FIXME 説明用のダイアログ等を表示しないといけない
							}
							break;
						case AndroidUtils.PermissionGrantResult.PERMISSION_DENY_AND_NEVER_ASK_AGAIN:
							break;
					}
				});
			}
			else
			{
				InitPlugin();
			}

			yield break;
		}

		/**
		 * プラグインを初期化
		 * uvc-plugin-unityへの処理要求
		 */
		private void InitPlugin()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}InitPlugin:");
#endif
			// IUVCDrawersが割り当てられているかどうかをチェック
			var hasDrawer = false;
			if ((UVCDrawers != null) && (UVCDrawers.Length > 0))
			{
				foreach (var drawer in UVCDrawers)
				{
					if (drawer is IUVCDrawer)
					{
						hasDrawer = true;
						break;
					}
				}
			}
			if (!hasDrawer)
			{   // インスペクタでIUVCDrawerが設定されていないときは
				// このスクリプトがaddされているゲームオブジェクトからの取得を試みる
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}InitPlugin:has no IUVCDrawer, try to get from gameObject");
#endif
				var drawers = GetComponents(typeof(IUVCDrawer));
				if ((drawers != null) && (drawers.Length > 0))
				{
					UVCDrawers = new Component[drawers.Length];
					int i = 0;
					foreach (var drawer in drawers)
					{
						UVCDrawers[i++] = drawer;
					}
				}
			}
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}InitPlugin:num drawers={UVCDrawers.Length}");
#endif
			// aandusbのDeviceDetectorを読み込み要求
			using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_DETECTOR))
			{
				clazz.CallStatic("initUVCDeviceDetector",
					AndroidUtils.GetCurrentActivity());
			}
		}

		//--------------------------------------------------------------------------------
		// ネイティブプラグイン関係の定義・宣言
		//--------------------------------------------------------------------------------
		//コールバック関数の型を宣言
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate void OnDeviceChangedFunc(IntPtr devicePtr, bool attached);

		/**
		 * プラグインでのレンダーイベント取得用native(c/c++)関数
		 */
		[DllImport("unityuvcplugin")]
		private static extern IntPtr GetRenderEventFunc();
		/**
		 * プラグインのnative側登録関数
		 */
		[DllImport("unityuvcplugin")]
		private static extern IntPtr Register(OnDeviceChangedFunc callback);
		/**
		 * プラグインのnative側登録解除関数
		 */
		[DllImport("unityuvcplugin")]
		private static extern IntPtr Unregister(OnDeviceChangedFunc callback);
		/**
		 * 映像取得開始
		 */
		[DllImport("unityuvcplugin", EntryPoint ="Start")]
		private static extern Int32 Start(Int32 deviceId, Int32 tex);
		/**
		 * 映像取得終了
		 */
		[DllImport("unityuvcplugin", EntryPoint ="Stop")]
		private static extern Int32 Stop(Int32 deviceId);
		/**
		 * 映像サイズ設定
		 */
		[DllImport("unityuvcplugin")]
		private static extern Int32 Resize(Int32 deviceId, Int32 frameType, Int32 width, Int32 height);
	}   // UVCManager

}   // namespace Serenegiant.UVC
