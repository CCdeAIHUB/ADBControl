package com.adbcontrol.companion.screen

import android.app.Activity
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Bundle

class ScreenCaptureConsentActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        streamId = intent.getStringExtra(EXTRA_STREAM_ID) ?: "screen-default"
        val manager = getSystemService(MediaProjectionManager::class.java)
        startActivityForResult(manager.createScreenCaptureIntent(), REQUEST_MEDIA_PROJECTION)
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQUEST_MEDIA_PROJECTION && resultCode == RESULT_OK && data != null) {
            ScreenCaptureState.setProjection(this, streamId, resultCode, data)
        }
        finish()
    }

    companion object {
        const val EXTRA_STREAM_ID = "com.adbcontrol.companion.screen.EXTRA_STREAM_ID"
        private const val REQUEST_MEDIA_PROJECTION = 6001
        private var streamId: String = "screen-default"
    }
}
