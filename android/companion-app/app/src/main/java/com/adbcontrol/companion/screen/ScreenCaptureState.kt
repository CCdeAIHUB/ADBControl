package com.adbcontrol.companion.screen

import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager

object ScreenCaptureState {
    private var projection: MediaProjection? = null
    private var activeStreamId: String? = null

    fun setProjection(context: Context, streamId: String, resultCode: Int, data: Intent) {
        val manager = context.getSystemService(MediaProjectionManager::class.java)
        projection?.stop()
        projection = manager.getMediaProjection(resultCode, data)
        activeStreamId = streamId
    }

    fun isProjectionReady(): Boolean = projection != null

    fun currentStreamId(): String? = activeStreamId

    fun stop() {
        projection?.stop()
        projection = null
        activeStreamId = null
    }
}
