using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace AIHelper.Views;

public class ImageViewerWindow : Window, IComponentConnector
{
	private Point _dragStartPoint;

	private bool _isDragging;

	internal Border ImageContainer;

	internal Image MainImage;

	internal MatrixTransform ImgTransform;

	private bool _contentLoaded;

	public ImageViewerWindow(ImageSource source)
	{
		InitializeComponent();
		MainImage.Source = source;
		base.PreviewKeyDown += delegate(object s, KeyEventArgs e)
		{
			if (e.Key == Key.Escape)
			{
				Close();
			}
		};
	}

	private void Container_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2)
		{
			Close();
		}
	}

	private void Container_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		Matrix matrix = ImgTransform.Matrix;
		Point position = e.GetPosition(ImageContainer);
		double num = ((e.Delta > 0) ? 1.2 : (5.0 / 6.0));
		matrix.ScaleAt(num, num, position.X, position.Y);
		ImgTransform.Matrix = matrix;
	}

	private void Container_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_dragStartPoint = e.GetPosition(this);
		_isDragging = true;
		ImageContainer.CaptureMouse();
	}

	private void Container_MouseMove(object sender, MouseEventArgs e)
	{
		if (_isDragging)
		{
			Point position = e.GetPosition(this);
			Vector vector = position - _dragStartPoint;
			Matrix matrix = ImgTransform.Matrix;
			matrix.Translate(vector.X, vector.Y);
			ImgTransform.Matrix = matrix;
			_dragStartPoint = position;
		}
	}

	private void Container_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		_isDragging = false;
		ImageContainer.ReleaseMouseCapture();
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/imageviewerwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			ImageContainer = (Border)target;
			ImageContainer.MouseLeftButtonDown += Container_MouseLeftButtonDown;
			ImageContainer.MouseLeftButtonUp += Container_MouseLeftButtonUp;
			ImageContainer.MouseMove += Container_MouseMove;
			ImageContainer.MouseWheel += Container_MouseWheel;
			ImageContainer.MouseDown += Container_MouseDown;
			break;
		case 2:
			MainImage = (Image)target;
			break;
		case 3:
			ImgTransform = (MatrixTransform)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
