using UnityEngine;
using UnityEngine.Rendering;
using static PostFXSettings;

public partial class PostFXStack {

	const string bufferName = "Post FX";
	CommandBuffer buffer = new CommandBuffer {
		name = bufferName
	};
	ScriptableRenderContext context;
	Camera camera;
	PostFXSettings settings;
    
    public bool IsActive => settings != null;
	bool useHDR;
	int colorLUTResolution;

	enum Pass {
		BloomHorizontal,
		BloomVertical,
		BloomAdd,
		BloomScatter,
		BloomScatterFinal,
		BloomPrefilter,
		BloomPrefilterFireflies,
		ColorGradingNone,
		ColorGradingACES,
		ColorGradingNeutral,
		ColorGradingReinhard,
		Final,
		Copy
	}

	int fxSourceId = Shader.PropertyToID("_PostFXSource"),
		fxSource2Id = Shader.PropertyToID("_PostFXSource2"),
		bloomBicubicUpsamplingId = Shader.PropertyToID("_BloomBicubicUpsampling"),
		bloomPrefilterId = Shader.PropertyToID("_BloomPrefilter"),
		bloomThresholdId = Shader.PropertyToID("_BloomThreshold"),
		bloomIntensityId = Shader.PropertyToID("_BloomIntensity"),
		bloomResultId = Shader.PropertyToID("_BloomResult"),
		colorAdjustmentsId = Shader.PropertyToID("_ColorAdjustments"),
		colorFilterId = Shader.PropertyToID("_ColorFilter"),
		whiteBalanceId = Shader.PropertyToID("_WhiteBalance"),
		splitToningShadowsId = Shader.PropertyToID("_SplitToningShadows"),
		splitToningHighlightsId = Shader.PropertyToID("_SplitToningHighlights"),
		channelMixerRedId = Shader.PropertyToID("_ChannelMixerRed"),
		channelMixerGreenId = Shader.PropertyToID("_ChannelMixerGreen"),
		channelMixerBlueId = Shader.PropertyToID("_ChannelMixerBlue"),
		smhShadowsId = Shader.PropertyToID("_SMHShadows"),
		smhMidtonesId = Shader.PropertyToID("_SMHMidtones"),
		smhHighlightsId = Shader.PropertyToID("_SMHHighlights"),
		smhRangeId = Shader.PropertyToID("_SMHRange"),
		colorGradingLUTId = Shader.PropertyToID("_ColorGradingLUT"),
		colorGradingLUTParametersId = Shader.PropertyToID("_ColorGradingLUTParameters"),
		colorGradingLUTInLogId = Shader.PropertyToID("_ColorGradingLUTInLog");

	const int maxBloomPyramidLevels = 16;
	int bloomPyramidId;

	public PostFXStack () 
	{
		bloomPyramidId = Shader.PropertyToID("_BloomPyramid0");
		for (int i = 1; i < maxBloomPyramidLevels * 2; i++) {
			Shader.PropertyToID("_BloomPyramid" + i);
		}
	}

	public void Setup (ScriptableRenderContext context, Camera camera, PostFXSettings settings, bool useHDR, int colorLUTResolution) 
    {
		this.context = context;
		this.camera = camera;
		this.useHDR = useHDR;
		// checking whether we have a game or scene camera
		// so that post FX get applied to proper cameras and nothing else
		this.settings = camera.cameraType <= CameraType.SceneView ? settings : null;
		this.colorLUTResolution = colorLUTResolution;
		
		ApplySceneViewState();
	}

    public void Render (int sourceId) {
        // Simply copy whatever's rendered up to this point to the camera's frame buffer.
		// buffer.Blit(sourceId, BuiltinRenderTextureType.CameraTarget);
		
		if (DoBloom(sourceId)) {
			DoColorGradingAndToneMapping(bloomResultId);
			buffer.ReleaseTemporaryRT(bloomResultId);
		}
		else {
			DoColorGradingAndToneMapping(sourceId);
		}

		context.ExecuteCommandBuffer(buffer);
		buffer.Clear();

        // In this case we don't need to manually begin and end buffer samples, 
        // as we don't need to invoke ClearRenderTarget because we completely replace what was at the destination.
	}

	void Draw (RenderTargetIdentifier from, RenderTargetIdentifier to, Pass pass) 
	{
		buffer.SetGlobalTexture(fxSourceId, from);
		buffer.SetRenderTarget(
			to, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
		);
		buffer.DrawProcedural(
			Matrix4x4.identity, settings.Material, (int)pass,
			MeshTopology.Triangles, 3
		);
	}

	// Additive Blurring
	bool DoBloom (int sourceId) 
	{
		PostFXSettings.BloomSettings bloom = settings.Bloom;
		
		int width = camera.pixelWidth / 2, height = camera.pixelHeight / 2;

		// Determine whether to stop generating the pyramid
		if ( bloom.maxIterations == 0 || bloom.intensity <= 0f ||
			 height < bloom.downscaleLimit * 2 || width < bloom.downscaleLimit * 2) 
		{
			// if so, directly drawing to the camera target when the effect is skipped.
			// Draw(sourceId, BuiltinRenderTextureType.CameraTarget, Pass.Copy);
			// buffer.EndSample("Bloom");
			return false;
		}
		
		buffer.BeginSample("Bloom");

		// Intensity threshold. Only when the intensity is higher then threshold the bloom effect is applied.
		Vector4 threshold;
		threshold.x = Mathf.GammaToLinearSpace(bloom.threshold);
		threshold.y = threshold.x * bloom.thresholdKnee;
		threshold.z = 2f * threshold.y;
		threshold.w = 0.25f / (threshold.y + 0.00001f);
		threshold.y -= threshold.x;
		buffer.SetGlobalVector(bloomThresholdId, threshold);

		RenderTextureFormat format = useHDR ?
			RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;

		// Make the half-size image as a pre-filter texture and use that for the start of the pyramid
		// Apply the threshold to pick up the point we need to bloom
		buffer.GetTemporaryRT(
			bloomPrefilterId, width, height, 0, FilterMode.Bilinear, format
		);
		Draw(sourceId, bloomPrefilterId, bloom.fadeFireflies ? Pass.BloomPrefilterFireflies : Pass.BloomPrefilter);
		width /= 2;
		height /= 2;

		// Horizontal to next level, vertical at same level.
		int fromId = bloomPrefilterId, toId = bloomPyramidId + 1;

		// Pyramid
		int i;
		for (i = 0; i < bloom.maxIterations; i++) 
		{
			if (height < bloom.downscaleLimit || width < bloom.downscaleLimit) {
				break;
			}

			// Save Horizontal Blur result in the same level in the pyramid
			int midId = toId - 1;
			buffer.GetTemporaryRT(
				midId, width, height, 0, FilterMode.Bilinear, format
			);

			buffer.GetTemporaryRT(
				toId, width, height, 0, FilterMode.Bilinear, format
			);
			
			Draw(fromId, midId, Pass.BloomHorizontal);
			Draw(midId, toId, Pass.BloomVertical);

			fromId = toId;
			toId += 2;
			width /= 2;
			height /= 2;
		}
		
		// Release the texture used for the horizontal draw of the last iteration
		buffer.ReleaseTemporaryRT(fromId - 1);
		buffer.ReleaseTemporaryRT(bloomPrefilterId);
		// Set the destination to the texture used for the horizontal draw one level lower.
		toId -= 5;

		// Set the flag to determine whether to upsample with bicubic filter
		buffer.SetGlobalFloat(bloomBicubicUpsamplingId, bloom.bicubicUpsampling ? 1f : 0f);

		Pass combinePass, finalPass;
		float finalIntensity;
		if (bloom.mode == PostFXSettings.BloomSettings.Mode.Additive) {
			combinePass = finalPass = Pass.BloomAdd;
			buffer.SetGlobalFloat(bloomIntensityId, 1f);
			finalIntensity = bloom.intensity;
		}
		else {
			combinePass = Pass.BloomScatter;
			finalPass = Pass.BloomScatterFinal;
			buffer.SetGlobalFloat(bloomIntensityId, bloom.scatter);
			finalIntensity = Mathf.Min(bloom.intensity, 0.95f);
		}

		// When we loop back we draw again each iteration, in the opposite direction
		// Upsamping, only reserve the horizontal result in each level 
		if (i > 1) {
			for (i -= 1; i > 0; i--) {
				buffer.SetGlobalTexture(fxSource2Id, toId + 1);
				Draw(fromId, toId, combinePass);
				buffer.ReleaseTemporaryRT(fromId);
				buffer.ReleaseTemporaryRT(toId + 1);
				fromId = toId;
				toId -= 2;
			}
		}
		else {
			buffer.ReleaseTemporaryRT(bloomPyramidId);
		}
		
		buffer.SetGlobalFloat(bloomIntensityId, finalIntensity);
		buffer.SetGlobalTexture(fxSource2Id, sourceId);
		buffer.GetTemporaryRT(
			bloomResultId, camera.pixelWidth, camera.pixelHeight, 0,
			FilterMode.Bilinear, format
		);
		Draw(fromId, bloomResultId, finalPass);
		buffer.ReleaseTemporaryRT(fromId);

		buffer.EndSample("Bloom");
		return true;
	}

	// Gussian Pyramid
	/*
	void DoBloom (int sourceId) 
	{
		buffer.BeginSample("Bloom");
		PostFXSettings.BloomSettings bloom = settings.Bloom;
		
		int width = camera.pixelWidth / 2, height = camera.pixelHeight / 2;
		RenderTextureFormat format = RenderTextureFormat.Default;

		// Horizontal to next level, vertical at same level.
		int fromId = sourceId, toId = bloomPyramidId + 1;

		// Pyramid
		int i;
		for (i = 0; i < bloom.maxIterations; i++) 
		{
			if (height < bloom.downscaleLimit || width < bloom.downscaleLimit) {
				break;
			}

			// Save Horizontal Blur result in the same level in the pyramid
			int midId = toId - 1;
			buffer.GetTemporaryRT(
				midId, width, height, 0, FilterMode.Bilinear, format
			);

			buffer.GetTemporaryRT(
				toId, width, height, 0, FilterMode.Bilinear, format
			);
			
			Draw(fromId, midId, Pass.BloomHorizontal);
			Draw(midId, toId, Pass.BloomVertical);

			fromId = toId;
			toId += 2;
			width /= 2;
			height /= 2;
		}
		
		
		Draw(fromId, BuiltinRenderTextureType.CameraTarget, Pass.Copy);
		for (i -= 1; i >= 0; i--) {
			buffer.ReleaseTemporaryRT(fromId);
			buffer.ReleaseTemporaryRT(fromId - 1);
			fromId -= 2;
		}
		buffer.EndSample("Bloom");
	}
	*/

	void DoColorGradingAndToneMapping(int sourceId) {
		ConfigureColorAdjustments();
		ConfigureWhiteBalance();
		ConfigureSplitToning();
		ConfigureChannelMixer();
		ConfigureShadowsMidtonesHighlights();

		int lutHeight = colorLUTResolution;
		// The LUT is 3D, we'll use a wide 2D texture instead to simulate a 3D texture, by placing 2D slices in a row.
		int lutWidth = lutHeight * lutHeight;
		buffer.GetTemporaryRT(
			colorGradingLUTId, lutWidth, lutHeight, 0,
			FilterMode.Bilinear, RenderTextureFormat.DefaultHDR
		);
		buffer.SetGlobalVector(
			colorGradingLUTParametersId, new Vector4(
			lutHeight, 0.5f / lutWidth, 0.5f / lutHeight, lutHeight / (lutHeight - 1f)
		));

		ToneMappingSettings.Mode mode = settings.ToneMapping.mode;
		Pass pass = Pass.ColorGradingNone + (int)mode;
		buffer.SetGlobalFloat(
			colorGradingLUTInLogId, useHDR && pass != Pass.ColorGradingNone ? 1f : 0f
		);
		// Render both color grading and tone mapping to the LUT.
		Draw(sourceId, colorGradingLUTId, pass);

		buffer.SetGlobalVector(colorGradingLUTParametersId,
			new Vector4(1f / lutWidth, 1f / lutHeight, lutHeight - 1f)
		);
		// Get the unadjusted image as the final result 
		Draw(sourceId, BuiltinRenderTextureType.CameraTarget, Pass.Final);
		buffer.ReleaseTemporaryRT(colorGradingLUTId);

	}

	void ConfigureColorAdjustments () {
		ColorAdjustmentsSettings colorAdjustments = settings.ColorAdjustments;
		buffer.SetGlobalVector(colorAdjustmentsId, new Vector4(
			Mathf.Pow(2f, colorAdjustments.postExposure),
			colorAdjustments.contrast * 0.01f + 1f,
			colorAdjustments.hueShift * (1f / 360f),
			colorAdjustments.saturation * 0.01f + 1f
		));
		buffer.SetGlobalColor(colorFilterId, colorAdjustments.colorFilter.linear);
	}

	void ConfigureWhiteBalance () {
		WhiteBalanceSettings whiteBalance = settings.WhiteBalance;
		buffer.SetGlobalVector(whiteBalanceId, ColorUtils.ColorBalanceToLMSCoeffs(
			whiteBalance.temperature, whiteBalance.tint
		));
	}

	void ConfigureSplitToning () {
		SplitToningSettings splitToning = settings.SplitToning;
		Color splitColor = splitToning.shadows;
		splitColor.a = splitToning.balance * 0.01f;
		buffer.SetGlobalColor(splitToningShadowsId, splitColor);
		buffer.SetGlobalColor(splitToningHighlightsId, splitToning.highlights);
	}
	
	void ConfigureChannelMixer () {
		ChannelMixerSettings channelMixer = settings.ChannelMixer;
		buffer.SetGlobalVector(channelMixerRedId, channelMixer.red);
		buffer.SetGlobalVector(channelMixerGreenId, channelMixer.green);
		buffer.SetGlobalVector(channelMixerBlueId, channelMixer.blue);
	}

	void ConfigureShadowsMidtonesHighlights () {
		ShadowsMidtonesHighlightsSettings smh = settings.ShadowsMidtonesHighlights;
		buffer.SetGlobalColor(smhShadowsId, smh.shadows.linear);
		buffer.SetGlobalColor(smhMidtonesId, smh.midtones.linear);
		buffer.SetGlobalColor(smhHighlightsId, smh.highlights.linear);
		buffer.SetGlobalVector(smhRangeId, new  Vector4 (
			smh.shadowsStart, smh.shadowsEnd, smh.highlightsStart, smh.highLightsEnd
		));
	}
}