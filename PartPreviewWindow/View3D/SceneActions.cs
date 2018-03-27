﻿/*
Copyright (c) 2017, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.DataConverters3D.UndoCommands;
using MatterHackers.MatterControl.DesignTools.Operations;
using MatterHackers.PolygonMesh;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.PartPreviewWindow
{
	public static class SceneActions
	{
		public static async void UngroupSelection(this InteractiveScene Scene)
		{
			if (Scene.HasSelection)
			{
				await Task.Run(() =>
				{
					var selectedItem = Scene.SelectedItem;
					bool isGroupItemType = Scene.HasSelection && selectedItem.Children.Count > 0;

					// If not a Group ItemType, look for mesh volumes and split into distinct objects if found
					if (!isGroupItemType 
						&& !selectedItem.HasChildren()
						&& selectedItem.Mesh != null)
					{
						var ungroupItem = Scene.SelectedItem;
						// clear the selection
						Scene.SelectedItem = null;

						// try to cut it up into multiple meshes
						var discreetMeshes = CreateDiscreteMeshes.SplitVolumesIntoMeshes(ungroupItem.Mesh, CancellationToken.None, (double progress0To1, string processingState) =>
						{
							//view3DWidget.ReportProgressChanged(progress0To1 * .5, processingState);
						});

						if (discreetMeshes.Count == 1)
						{
							// restore the selection
							Scene.SelectedItem = ungroupItem;
							// No further processing needed, nothing to ungroup
							return;
						}

						// build the ungroup list
						List<IObject3D> addItems = new List<IObject3D>(discreetMeshes.Select(mesh => new Object3D()
						{
							Mesh = mesh,
							Matrix = ungroupItem.Matrix,
						}));
						
						// add and do the undo data
						Scene.UndoBuffer.AddAndDo(new ReplaceCommand(new List<IObject3D> { ungroupItem }, addItems));
					}

					if (isGroupItemType)
					{
						// Create and perform the delete operation
						// Store the operation for undo/redo
						Scene.UndoBuffer.AddAndDo(new UngroupCommand(Scene, Scene.SelectedItem));
					}
				});

				// leave no selection
				Scene.SelectedItem = null;
			}
		}

		public static async void GroupSelection(this InteractiveScene Scene)
		{
			if (Scene.HasChildren())
			{
				var selectedItem = Scene.SelectedItem;

				await Task.Run(() =>
				{
					// Create and perform the delete operation
					var operation = new GroupCommand(Scene, selectedItem);

					// Store the operation for undo/redo
					Scene.UndoBuffer.AddAndDo(operation);
				});
			}
		}

		public static async void AutoArrangeChildren(this InteractiveScene Scene, View3DWidget view3DWidget)
		{
			await Task.Run(() =>
			{
				PlatingHelper.ArrangeOnBed(Scene.Children.ToList(), Scene, view3DWidget.BedCenter);
			});
		}

		public static void Cut(this InteractiveScene scene, IObject3D sourceItem = null)
		{
			Clipboard.SetText("!--IObjectSelection--!");
			ApplicationController.ClipboardItem = scene.SelectedItem.Clone();

			scene.DeleteSelection();
		}

		public static void Copy(this InteractiveScene scene, IObject3D sourceItem = null)
		{
			Clipboard.SetText("!--IObjectSelection--!");
			ApplicationController.ClipboardItem = scene.SelectedItem.Clone();
		}

		public static void Paste(this InteractiveScene scene)
		{
			if (Clipboard.GetText() == "!--IObjectSelection--!")
			{
				scene.DuplicateItem(ApplicationController.ClipboardItem);
			}
		}

		public static async void DuplicateItem(this InteractiveScene Scene, IObject3D sourceItem = null)
		{
			if (sourceItem == null)
			{
				if (Scene.HasSelection)
				{
					sourceItem = Scene.SelectedItem;
				}
			}

			if (sourceItem != null)
			{
				// Copy selected item
				IObject3D newItem = await Task.Run(() =>
				{
					if (sourceItem != null)
					{
						if (sourceItem is SelectionGroup)
						{
							// the selection is a group of objects that need to be copied
							var copyList = sourceItem.Children.ToList();
							Scene.SelectedItem = null;
							foreach(var item in copyList)
							{
								var clonedItem = item.Clone();
								// make the name unique
								var newName = agg_basics.GetNonCollidingName(item.Name, Scene.DescendantsAndSelf().Select((d) => d.Name));
								clonedItem.Name = newName;
								// add it to the scene
								Scene.Children.Add(clonedItem);
								// add it to the selection
								Scene.AddToSelection(clonedItem);
							}
						}
						else // the selection can be cloned easily
						{
							var clonedItem = sourceItem.Clone();

							// make the name unique
							var newName = agg_basics.GetNonCollidingName(sourceItem.Name, Scene.DescendantsAndSelf().Select((d) => d.Name));
							clonedItem.Name = newName;

							// More useful if it creates the part in the exact position and then the user can move it.
							// Consistent with other software as well. LBB 2017-12-02
							//PlatingHelper.MoveToOpenPositionRelativeGroup(clonedItem, Scene.Children);

							return clonedItem;
						}
					}

					return null;
				});

				// it might come back null due to threading
				if (newItem != null)
				{
					Scene.InsertNewItem(newItem);
				}
			}
		}

		public static void InsertNewItem(this InteractiveScene Scene, IObject3D newItem)
		{
			// Reposition first item to bed center
			if (Scene.Children.Count == 0)
			{
				var printer = ApplicationController.Instance.ActivePrinter;
				var aabb = newItem.GetAxisAlignedBoundingBox(Matrix4X4.Identity);
				var center = aabb.Center;

				newItem.Matrix *= Matrix4X4.CreateTranslation(
					(printer.Bed.BedCenter.X + center.X),
					(printer.Bed.BedCenter.Y + center.Y),
					 -aabb.minXYZ.Z);
			}

			// Create and perform a new insert operation
			var insertOperation = new InsertCommand(Scene, newItem);
			insertOperation.Do();

			// Store the operation for undo/redo
			Scene.UndoBuffer.Add(insertOperation);
		}

		public static void DeleteSelection(this InteractiveScene Scene)
		{
			if (Scene.HasSelection)
			{
				// Create and perform the delete operation 
				var deleteOperation = new DeleteCommand(Scene, Scene.SelectedItem);

				// Store the operation for undo/redo
				Scene.UndoBuffer.AddAndDo(deleteOperation);

				Scene.ClearSelection();
			}
		}

		public static void MakeLowestFaceFlat(this InteractiveScene scene, IObject3D objectToLayFlatGroup)
		{
			bool firstVertex = true;

			IObject3D objectToLayFlat = objectToLayFlatGroup;

			IVertex lowestVertex = null;
			Vector3 lowestVertexPosition = Vector3.Zero;
			IObject3D itemToLayFlat = null;

			// Process each child, checking for the lowest vertex
			foreach (var itemToCheck in objectToLayFlat.VisibleMeshes())
			{
				// find the lowest point on the model
				for (int testIndex = 0; testIndex < itemToCheck.mesh.Vertices.Count; testIndex++)
				{
					var vertex = itemToCheck.mesh.Vertices[testIndex];
					Vector3 vertexPosition = Vector3.Transform(vertex.Position, itemToCheck.object3D.WorldMatrix());
					if (firstVertex)
					{
						lowestVertex = itemToCheck.mesh.Vertices[testIndex];
						lowestVertexPosition = vertexPosition;
						itemToLayFlat = itemToCheck.object3D;
						firstVertex = false;
					}
					else if (vertexPosition.Z < lowestVertexPosition.Z)
					{
						lowestVertex = itemToCheck.mesh.Vertices[testIndex];
						lowestVertexPosition = vertexPosition;
						itemToLayFlat = itemToCheck.object3D;
					}
				}
			}

			if (lowestVertex == null)
			{
				// didn't find any selected mesh
				return;
			}

			PolygonMesh.Face faceToLayFlat = null;
			double lowestAngleOfAnyFace = double.MaxValue;
			// Check all the faces that are connected to the lowest point to find out which one to lay flat.
			foreach (var face in lowestVertex.ConnectedFaces())
			{
				double biggestAngleToFaceVertex = double.MinValue;
				foreach (IVertex faceVertex in face.Vertices())
				{
					if (faceVertex != lowestVertex)
					{
						Vector3 faceVertexPosition = Vector3.Transform(faceVertex.Position, itemToLayFlat.WorldMatrix());
						Vector3 pointRelLowest = faceVertexPosition - lowestVertexPosition;
						double xLeg = new Vector2(pointRelLowest.X, pointRelLowest.Y).Length;
						double yLeg = pointRelLowest.Z;
						double angle = Math.Atan2(yLeg, xLeg);
						if (angle > biggestAngleToFaceVertex)
						{
							biggestAngleToFaceVertex = angle;
						}
					}
				}
				if (biggestAngleToFaceVertex < lowestAngleOfAnyFace)
				{
					lowestAngleOfAnyFace = biggestAngleToFaceVertex;
					faceToLayFlat = face;
				}
			}

			double maxDistFromLowestZ = 0;
			List<Vector3> faceVertices = new List<Vector3>();
			foreach (IVertex vertex in faceToLayFlat.Vertices())
			{
				Vector3 vertexPosition = Vector3.Transform(vertex.Position, itemToLayFlat.WorldMatrix());
				faceVertices.Add(vertexPosition);
				maxDistFromLowestZ = Math.Max(maxDistFromLowestZ, vertexPosition.Z - lowestVertexPosition.Z);
			}

			if (maxDistFromLowestZ > .001)
			{
				Vector3 xPositive = (faceVertices[1] - faceVertices[0]).GetNormal();
				Vector3 yPositive = (faceVertices[2] - faceVertices[0]).GetNormal();
				Vector3 planeNormal = Vector3.Cross(xPositive, yPositive).GetNormal();

				// this code takes the minimum rotation required and looks much better.
				Quaternion rotation = new Quaternion(planeNormal, new Vector3(0, 0, -1));
				Matrix4X4 partLevelMatrix = Matrix4X4.CreateRotation(rotation);

				// rotate it
				objectToLayFlatGroup.ApplyAtBoundsCenter(partLevelMatrix);

				scene.Invalidate();
			}

			PlatingHelper.PlaceOnBed(objectToLayFlatGroup);
		}

		internal class ArrangeUndoCommand : IUndoRedoCommand
		{
			private List<TransformCommand> allUndoTransforms = new List<TransformCommand>();

			public ArrangeUndoCommand(View3DWidget view3DWidget, List<Matrix4X4> preArrangeTarnsforms, List<Matrix4X4> postArrangeTarnsforms)
			{
				for (int i = 0; i < preArrangeTarnsforms.Count; i++)
				{
					//allUndoTransforms.Add(new TransformUndoCommand(view3DWidget, i, preArrangeTarnsforms[i], postArrangeTarnsforms[i]));
				}
			}

			public void Do()
			{
				for (int i = 0; i < allUndoTransforms.Count; i++)
				{
					allUndoTransforms[i].Do();
				}
			}

			public void Undo()
			{
				for (int i = 0; i < allUndoTransforms.Count; i++)
				{
					allUndoTransforms[i].Undo();
				}
			}
		}
	}
}
