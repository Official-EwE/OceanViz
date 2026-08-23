using UnityEngine;

namespace OceanViz3
{

/// <summary>
/// Displays a lightweight frames-per-second counter in the top-left corner.
/// </summary>
public sealed class FpsCounter : MonoBehaviour
{
	private const int SampleFrameCount = 30;
	private const float CounterWidth = 104.0f;
	private const float CounterHeight = 32.0f;
	private const float ScreenMargin = 8.0f;
	private const float LabelInset = 8.0f;

	private float accumulatedFrameTime;
	private int accumulatedFrames;
	private string counterText = "FPS: --";
	private GUIStyle counterStyle;

	/// <summary>
	/// Enables or disables counter sampling and display.
	/// </summary>
	public void SetVisible(bool visible)
	{
		enabled = visible;
	}

	private void OnEnable()
	{
		ResetMeasurement();
	}

	private void Update()
	{
		accumulatedFrameTime += Mathf.Max(Time.unscaledDeltaTime, Mathf.Epsilon);
		accumulatedFrames++;
		if (accumulatedFrames < SampleFrameCount)
		{
			return;
		}

		int framesPerSecond = Mathf.RoundToInt(accumulatedFrames / accumulatedFrameTime);
		counterText = $"FPS: {framesPerSecond}";
		accumulatedFrameTime = 0.0f;
		accumulatedFrames = 0;
	}

	private void OnGUI()
	{
		if (counterStyle == null)
		{
			counterStyle = new GUIStyle(GUI.skin.label);
			counterStyle.fontSize = 18;
			counterStyle.normal.textColor = Color.white;
		}

		float counterX = Screen.width - CounterWidth - ScreenMargin;
		Rect backgroundRect = new Rect(counterX, ScreenMargin, CounterWidth, CounterHeight);
		Rect labelRect = new Rect(counterX + LabelInset, ScreenMargin + 4.0f, CounterWidth - LabelInset, 24.0f);
		Color previousColor = GUI.color;
		GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.65f);
		GUI.DrawTexture(backgroundRect, Texture2D.whiteTexture);
		GUI.color = previousColor;
		GUI.Label(labelRect, counterText, counterStyle);
	}

	private void ResetMeasurement()
	{
		accumulatedFrameTime = 0.0f;
		accumulatedFrames = 0;
		counterText = "FPS: --";
	}
}

}
