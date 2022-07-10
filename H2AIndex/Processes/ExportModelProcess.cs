﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assimp;
using H2AIndex.Common;
using H2AIndex.Common.Enumerations;
using H2AIndex.Models;
using Saber3D.Data.Textures;
using Saber3D.Files;
using Saber3D.Files.FileTypes;
using Saber3D.Serializers.Configurations;

namespace H2AIndex.Processes
{

  public class ExportModelProcess : ProcessBase
  {

    #region Data Members

    private IS3DFile _file;
    private Scene _scene;
    private ModelExportOptionsModel _modelOptions;
    private TextureExportOptionsModel _textureOptions;

    private string _outputPath;

    #endregion

    #region Constructor

    public ExportModelProcess( IS3DFile file, Tuple<ModelExportOptionsModel, TextureExportOptionsModel> options )
    {
      _file = file;
      _modelOptions = options.Item1.DeepCopy();
      _textureOptions = options.Item2.DeepCopy();

      _textureOptions.OutputPath = _modelOptions.OutputPath;
      _textureOptions.ExportAllMips = false;
    }

    public ExportModelProcess( IS3DFile file, Scene scene, Tuple<ModelExportOptionsModel, TextureExportOptionsModel> options )
      : this( file, options )
    {
      _scene = scene;
    }

    #endregion

    #region Overrides

    protected override async Task OnInitializing()
    {
      _outputPath = GetOutputModelPath();
    }

    protected override async Task OnExecuting()
    {
      if ( _scene is null )
        await ConvertFileToAssimpScene();

      if ( _modelOptions.ExportTextures )
        FixupTextureSlotFileExtensions();

      await WriteAssimpSceneToFile();

      if ( _modelOptions.ExportTextures )
        await ExportTextures();
    }

    #endregion

    #region Private Methods

    private string GetOutputModelPath()
    {
      var extension = _modelOptions.OutputFileFormat.GetFileExtension();
      var fName = Path.ChangeExtension( _file.Name, extension );

      return Path.Combine( _modelOptions.OutputPath, fName );
    }

    private async Task ConvertFileToAssimpScene()
    {
      var process = new ConvertModelToAssimpSceneProcess( _file );
      BindStatusToSubProcess( process );

      await process.Execute();
      StatusList.Merge( process.StatusList );

      _scene = process.Result;
    }

    private async Task WriteAssimpSceneToFile()
    {
      try
      {
        Status = "Writing File";
        IsIndeterminate = true;

        var formatId = _modelOptions.OutputFileFormat.ToAssimpFormatId();
        using ( var context = new AssimpContext() )
        {
          context.ExportFile( _scene, _outputPath, formatId );
        }
      }
      catch ( Exception ex )
      {
        StatusList.AddError( _file.Name, "Failed to write the model file.", ex );
        throw;
      }
    }

    private async Task ExportTextures()
    {
      IsIndeterminate = true;
      Status = "Exporting Textures";

      var files = GatherTextures();
      var process = new BulkExportTexturesProcess( files, _textureOptions );
      BindStatusToSubProcess( process );

      await process.Execute();
      StatusList.Merge( process.StatusList );
    }

    private IEnumerable<IS3DFile> GatherTextures()
    {
      var textures = new Dictionary<string, IS3DFile>();

      // Get base textures
      foreach ( var material in _scene.Materials )
      {
        if ( material.Name == "DefaultMaterial" )
          continue;

        var textureFiles = H2AFileContext.Global
          .GetFiles( material.Name )
          .OfType<PictureFile>();

        foreach ( var file in textureFiles )
          textures.TryAdd( file.Name, file );
      }

      // Get Detail Maps and addl textures from TextureDefinition
      var textureNames = textures.Keys.ToArray();
      foreach ( var textureName in textureNames )
        GatherDetailMaps( textureName, textures );

      return textures.Values;
    }

    private void GatherDetailMaps( string parentTextureName, Dictionary<string, IS3DFile> textures )
    {
      var tdFile = H2AFileContext.Global.GetFile( Path.ChangeExtension( parentTextureName, ".td" ) );
      if ( tdFile is null )
        return;

      var texDef = new FileScriptingSerializer<TextureDefinition>().Deserialize( tdFile.GetStream() );
      foreach ( var textureName in texDef.GetTextureNames() )
      {
        var nameWithExt = Path.ChangeExtension( textureName, "pct" );
        if ( textures.ContainsKey( nameWithExt ) )
          continue;

        var textureFile = H2AFileContext.Global.GetFile( nameWithExt );
        if ( textureFile is null )
          continue;

        textures.Add( textureFile.Name, textureFile );
      }
    }

    private void FixupTextureSlotFileExtensions()
    {
      foreach ( var material in _scene.Materials )
      {
        material.TextureDiffuse = FixupTextureSlotFileExtension( material.TextureDiffuse );
        material.TextureNormal = FixupTextureSlotFileExtension( material.TextureNormal );
        material.TextureSpecular = FixupTextureSlotFileExtension( material.TextureSpecular );
      }
    }

    private TextureSlot FixupTextureSlotFileExtension( TextureSlot slot )
    {
      if ( string.IsNullOrEmpty( slot.FilePath ) )
        return slot;

      var ext = _textureOptions.OutputFileFormat.ToString().ToLower();
      var newName = Path.ChangeExtension( slot.FilePath, ext );

      slot = new TextureSlot( newName, slot.TextureType, slot.TextureIndex, slot.Mapping,
        slot.UVIndex, slot.BlendFactor, slot.Operation, slot.WrapModeU, slot.WrapModeV, slot.Flags );
      return slot;
    }

    #endregion

  }

}